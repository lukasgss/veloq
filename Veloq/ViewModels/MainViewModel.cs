using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veloq.Data;

namespace Veloq.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    public const string NaiveQuery =
        """
        // Naive N+1: one query for the customers, then one query PER customer.
        // Rewrite so EF Core emits a single SQL statement, then Run again to
        // compare the plan and round-trip count.
        db.Customers
          .Where(c => c.Country == country)
          .OrderBy(c => c.Id)
          .AsEnumerable()
          .Select(c => new CustomerTotal
          {
              CustomerId = c.Id,
              Total = db.Orders.Where(o => o.CustomerId == c.Id).Sum(o => o.Total)
          })
        """;

    public MainViewModel() => QueryText = NaiveQuery;

    public ObservableCollection<ConnectionInfo> Connections { get; } = [];

    [ObservableProperty]
    public partial ConnectionInfo? SelectedConnection { get; set; }

    partial void OnSelectedConnectionChanged(ConnectionInfo? value)
    {
        StatusText = value is null
            ? "Add a connection to start."
            : $"Connected to {value.Name} ({value.Subtitle}).";

        RunCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial string QueryText { get; set; } = "";
    [ObservableProperty]
    public partial string Country { get; set; } = "US";
    [ObservableProperty]
    public partial bool IsRunning { get; set; }
    [ObservableProperty]
    public partial bool HasError { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Add a connection to start.";
    [ObservableProperty]
    public partial string SqlText { get; set; } = "";
    [ObservableProperty]
    public partial string PlanText { get; set; } = "";
    [ObservableProperty]
    public partial string ResultsText { get; set; } = "";

    [RelayCommand]
    private void Reset() => QueryText = NaiveQuery;

    private bool CanRun() => SelectedConnection is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        IsRunning = true;
        HasError = false;
        StatusText = "Running...";
        RunCommand.NotifyCanExecuteChanged();

        try
        {
            QueryResult result = await SelectedConnection.Runner.RunAsync(QueryText, Country);
            if (!result.Success)
            {
                HasError = true;
                StatusText = result.Error ?? "Unknown error.";
                SqlText = PlanText = ResultsText = "";
                return;
            }

            SqlText = result.Sql;
            PlanText = result.Plan;
            ResultsText = FormatTable(result);

            var warn = result.QueryCount > 1
                ? $"  ⚠ N+1 — {result.QueryCount} round-trips"
                : "  ✓ single query";
            StatusText =
                $"Query successful ({result.ElapsedMs / 1000.0:0.000} seconds) • {result.RowCount} rows • " +
                $"{result.QueryCount} quer{(result.QueryCount == 1 ? "y" : "ies")}{warn}";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    public partial bool IsAddingConnection { get; set; }

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial bool ConnectionFailed { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewHost { get; set; } = "localhost";

    [ObservableProperty]
    public partial string NewPort { get; set; } = "5432";

    [ObservableProperty]
    public partial string NewDatabase { get; set; } = "postgres";

    [ObservableProperty]
    public partial string NewUsername { get; set; } = "postgres";

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SeedSampleData { get; set; }

    [RelayCommand]
    private void ShowAddConnection()
    {
        ConnectionFailed = false;
        ConnectionStatus = string.Empty;
        IsAddingConnection = true;
    }

    [RelayCommand]
    private void CancelAddConnection() => IsAddingConnection = false;

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        IsConnecting = true;
        ConnectionFailed = false;
        ConnectionStatus = "Connecting…";
        try
        {
            string cs = $"Host={NewHost};Port={NewPort};Database={NewDatabase};Username={NewUsername};Password={NewPassword}";
            QueryRunner runner = new(cs);
            string version = await runner.TestConnectionAsync();

            if (SeedSampleData)
            {
                ConnectionStatus = "Seeding sample schema…";
                await runner.SeedSampleSchemaAsync();
            }

            ConnectionInfo conn = new()
            {
                Name = string.IsNullOrWhiteSpace(NewName) ? $"{NewHost}:{NewPort}" : NewName,
                Host = NewHost,
                Port = NewPort,
                Database = NewDatabase,
                Username = NewUsername,
                Password = NewPassword,
                Runner = runner,
            };

            Connections.Add(conn);
            SelectedConnection = conn;
            IsAddingConnection = false;
            StatusText = $"Connected to PostgreSQL {version} — {conn.Subtitle}.";
        }
        catch (Exception ex)
        {
            ConnectionFailed = true;
            ConnectionStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private static void Line(StringBuilder sb, IReadOnlyList<int> widths, IReadOnlyCollection<string> cells)
    {
        sb.AppendLine(string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))));
    }

    private static string FormatTable(QueryResult r)
    {
        if (r.RowCount == 0)
        {
            return "(no rows)";
        }

        int[] widths = r.Columns.Select((c, i) => Math.Max(c.Length, r.Rows.Max(row => i < row.Length ? row[i].Length : 0)))
            .ToArray();

        StringBuilder sb = new();

        Line(sb, widths, r.Columns);
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('─', w))));

        foreach (string[] row in r.Rows)
        {
            Line(sb, widths, row);
        }

        if (r.RowCount > r.Rows.Count)
        {
            sb.AppendLine($"... {r.RowCount - r.Rows.Count} more rows");
        }

        return sb.ToString().TrimEnd();
    }
}

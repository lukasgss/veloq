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
    public MainViewModel()
    {
        try
        {
            foreach (ConnectionInfo connection in ConnectionStore.Load())
            {
                Connections.Add(connection);
            }

            SelectedConnection = Connections.FirstOrDefault();
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = $"Could not load saved connections: {ex.Message}";
        }
    }

    public ObservableCollection<ConnectionInfo> Connections { get; } = [];

    [ObservableProperty]
    public partial ConnectionInfo? SelectedConnection { get; set; }

    partial void OnSelectedConnectionChanged(ConnectionInfo? value)
    {
        StatusText = value is null
            ? "Add a connection to start."
            : $"Selected {value.Name} ({value.Subtitle}).";

        RunCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsRunning { get; set; }
    [ObservableProperty]
    public partial bool HasError { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Add a connection to start.";
    [ObservableProperty]
    public partial string SqlText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string PlanText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string ResultsText { get; set; } = string.Empty;

    [RelayCommand]
    private void Reset() => QueryText = string.Empty;

    public Task<IReadOnlyList<CompletionSuggestion>> GetCompletionsAsync(string text, int position)
    {
        return SelectedConnection?.Runner.GetCompletionsAsync(text, position)
               ?? Task.FromResult<IReadOnlyList<CompletionSuggestion>>([]);
    }

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
            QueryResult result = await SelectedConnection.Runner.RunAsync(QueryText);
            if (!result.Success)
            {
                HasError = true;
                StatusText = result.Error ?? "Unknown error.";
                SqlText = PlanText = ResultsText = string.Empty;
                return;
            }

            SqlText = result.Sql;
            PlanText = result.Plan;
            ResultsText = FormatTable(result);

            string executionSummary;
            if (result.IsSplitQuery && result.QueryCount > 1)
            {
                executionSummary = $"{result.QueryCount} queries • ✓ intentional split query";
            }
            else if (result.QueryCount > 1)
            {
                executionSummary = $"{result.QueryCount} queries • ⚠ possible N+1";
            }
            else if (result.QueryCount == 1)
            {
                executionSummary = "1 query • ✓ single query";
            }
            else
            {
                executionSummary = "0 queries • client-side result";
            }

            StatusText = $"Query successful ({result.ElapsedMs / 1000.0:0.000} seconds) • {result.RowCount} rows • " + executionSummary;
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
            string cs = ConnectionInfo.BuildConnectionString(
                NewHost, NewPort, NewDatabase, NewUsername, NewPassword);
            QueryRunner runner = new(cs);
            string version = await runner.TestConnectionAsync();

            ConnectionStatus = "Reading database schema…";
            await runner.GetModelAsync();

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

            ConnectionStore.Save(Connections.Append(conn));
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

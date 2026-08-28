using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veloq.Data;

namespace Veloq.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly Func<string, QueryRunner> _createRunner;
    private readonly Action<IEnumerable<ConnectionInfo>> _saveConnections;
    private readonly Func<Action, Task> _dispatchToUi;

    public MainViewModel()
        : this(ConnectionStore.Load, connectionString => new QueryRunner(connectionString), ConnectionStore.Save)
    {
    }

    internal MainViewModel(
        Func<IReadOnlyList<ConnectionInfo>> loadConnections,
        Func<string, QueryRunner> createRunner,
        Action<IEnumerable<ConnectionInfo>> saveConnections,
        Func<Action, Task>? dispatchToUi = null)
    {
        _createRunner = createRunner;
        _saveConnections = saveConnections;
        _dispatchToUi = dispatchToUi ?? DispatchToUiAsync;

        try
        {
            foreach (ConnectionInfo connection in loadConnections())
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

        if (value is not null)
        {
            PrefetchTablesAndIntellisenseOffUiThread(value.Runner);
        }
    }

    private void PrefetchTablesAndIntellisenseOffUiThread(QueryRunner runner)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await runner.GetModelAsync();
            }
            catch (Exception ex)
            {
                await _dispatchToUi(() =>
                {
                    HasError = true;
                    StatusText = $"{ex.GetType().Name}: {ex.Message}";
                });
            }
        });
    }

    private static async Task DispatchToUiAsync(Action action) =>
        await Dispatcher.UIThread.InvokeAsync(action);

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsRunning { get; set; }
    [ObservableProperty]
    public partial bool HasError { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Add a connection to start.";

    public ObservableCollection<StatusSegment> StatusSegments { get; } = [];

    [ObservableProperty]
    public partial bool HasStatusSegments { get; set; }

    [ObservableProperty]
    public partial bool CompareTracking { get; set; }
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
        ClearStatusSegments();
        RunCommand.NotifyCanExecuteChanged();

        try
        {
            QueryResult result = await SelectedConnection.Runner.RunAsync(QueryText, BenchmarkOptions.Default);
            if (!result.Success)
            {
                HasError = true;
                StatusText = result.Error ?? "Unknown error.";
                ClearStatusSegments();
                SqlText = PlanText = ResultsText = string.Empty;
                return;
            }

            SqlText = result.Sql;
            PlanText = result.Plan;
            ResultsText = FormatTable(result);

            if (CompareTracking)
            {
                await ShowTrackingComparisonAsync(SelectedConnection.Runner, result);
            }
            else
            {
                ShowStatusSegments(result);
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = $"{ex.GetType().Name}: {ex.Message}";
            ClearStatusSegments();
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
            QueryRunner runner = _createRunner(cs);
            string version = await runner.TestConnectionAsync();

            ConnectionStatus = "Reading database schema…";
            await Task.Run(() => runner.GetModelAsync());

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

            _saveConnections(Connections.Append(conn));
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

    private static void Line(StringBuilder sb, int[] widths, IReadOnlyCollection<string> cells)
    {
        sb.AppendLine(string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))));
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string FormatMs(double ms) => ms >= 1000 ? $"{ms / 1000.0:0.00} s" : $"{ms:0.0} ms";

    private async Task ShowTrackingComparisonAsync(QueryRunner runner, QueryResult asWritten)
    {
        bool writtenIsUntracked = QueryDiagnostics.ContainsAsNoTracking(QueryText);

        BenchmarkOptions matched = BenchmarkOptions.Default with
        {
            MeasuredCount = asWritten.Benchmark?.MeasuredCount ?? BenchmarkOptions.Default.MeasuredCount,
            Adaptive = false,
        };

        QueryResult counterpart = writtenIsUntracked
            ? await runner.RunAsync(QueryDiagnostics.RemoveAsNoTracking(QueryText), matched)
            : await runner.RunAsync(QueryText, matched, noTracking: true);

        if (!counterpart.Success)
        {
            ShowStatusSegments(asWritten);
            return;
        }

        QueryResult tracked = writtenIsUntracked ? counterpart : asWritten;
        QueryResult untracked = writtenIsUntracked ? asWritten : counterpart;

        double trackedMs = MedianOf(tracked);
        double untrackedMs = MedianOf(untracked);

        StatusSegments.Clear();
        StatusText = string.Empty;
        StatusSegments.Add(new StatusSegment("tracked", FormatMs(trackedMs)));
        StatusSegments.Add(new StatusSegment("no tracking", FormatMs(untrackedMs)));
        StatusSegments.Add(new StatusSegment("difference", FormatDelta(trackedMs, untrackedMs)));
        StatusSegments.Add(new StatusSegment("returned", Plural(asWritten.RowCount, "row")));
        StatusSegments.Add(QueryCountSegment(asWritten));
        HasStatusSegments = true;
    }

    private static double MedianOf(QueryResult r) => r.Benchmark?.MedianMs ?? r.ElapsedMs;

    private static string FormatDelta(double trackedMs, double untrackedMs)
    {
        if (trackedMs <= 0)
        {
            return "n/a";
        }

        double percent = (untrackedMs - trackedMs) / trackedMs * 100;

        return percent.ToString("+0.#;-0.#;0") + "%";
    }

    private void ClearStatusSegments()
    {
        StatusSegments.Clear();
        HasStatusSegments = false;
    }

    private void ShowStatusSegments(QueryResult r)
    {
        StatusSegments.Clear();
        StatusText = string.Empty;

        if (r.Benchmark is { } b)
        {
            StatusSegments.Add(new StatusSegment("median", FormatMs(b.MedianMs)));
            StatusSegments.Add(new StatusSegment("p95", FormatMs(b.P95Ms)));
            StatusSegments.Add(new StatusSegment("range", $"{b.MinMs:0.0}–{FormatMs(b.MaxMs)}"));
            StatusSegments.Add(new StatusSegment("first run", FormatMs(b.ColdMs)));
            StatusSegments.Add(new StatusSegment("measured over", Plural(b.MeasuredCount, "run")));
        }
        else
        {
            StatusSegments.Add(new StatusSegment("took", FormatMs(r.ElapsedMs)));
        }

        StatusSegments.Add(new StatusSegment("returned", Plural(r.RowCount, "row")));
        StatusSegments.Add(QueryCountSegment(r));
        HasStatusSegments = true;
    }

    private static StatusSegment QueryCountSegment(QueryResult r)
    {
        if (r.QueryCount == 0)
        {
            return new StatusSegment("using", "no query (client-side)");
        }

        if (r.QueryCount > 1 && !r.IsSplitQuery)
        {
            return new StatusSegment("using", $"{r.QueryCount} queries — possible N+1", IsWarning: true);
        }

        return r.IsSplitQuery
            ? new StatusSegment("using", $"{r.QueryCount} queries (split)")
            : new StatusSegment("using", "1 query");
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

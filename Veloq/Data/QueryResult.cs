namespace Veloq.Data;

public sealed class QueryResult
{
    public bool Success { get; init; }
    public string? Error { get; private init; }
    public string Sql { get; init; } = string.Empty;
    public string Plan { get; init; } = string.Empty;
    public int QueryCount { get; init; }
    public bool IsSplitQuery { get; init; }
    public int RowsFetched { get; init; }
    public int ReturnedCount { get; init; }
    public int CollectionIncludeCount { get; init; }
    public bool IsCartesianRisk { get; init; }
    public bool IsCartesian { get; init; }
    public long ElapsedMs { get; init; }
    public BenchmarkResult? Benchmark { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<string[]> Rows { get; init; } = [];
    public int RowCount { get; init; }

    public static QueryResult Fail(string error) => new() { Success = false, Error = error };
}

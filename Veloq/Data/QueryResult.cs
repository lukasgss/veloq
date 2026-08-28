using System.Collections.Generic;

namespace Veloq.Data;

public sealed class QueryResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Sql { get; init; } = string.Empty;
    public string Plan { get; init; } = string.Empty;
    public int QueryCount { get; init; }
    public long ElapsedMs { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = new List<string>();
    public IReadOnlyList<string[]> Rows { get; init; } = new List<string[]>();
    public int RowCount { get; init; }

    public static QueryResult Fail(string error) => new() { Success = false, Error = error };
}

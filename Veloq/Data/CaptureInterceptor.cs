using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Veloq.Data;

/// <summary>Records every SQL command EF executes so we can count round-trips (N+1)
/// and re-run the last statement through EXPLAIN ANALYZE.</summary>
public sealed class CaptureInterceptor : DbCommandInterceptor
{
    public int QueryCount { get; private set; }
    public string? LastSql { get; private set; }
    public List<(string Name, object? Value)> LastParams { get; private set; } = [];

    public void Reset()
    {
        QueryCount = 0;
        LastSql = null;
        LastParams = [];
    }

    private void Capture(DbCommand command)
    {
        QueryCount++;
        LastSql = command.CommandText;
        LastParams = command.Parameters
            .Cast<DbParameter>()
            .Select(p => (p.ParameterName, p.Value))
            .ToList();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return result;
    }

    public override System.Threading.Tasks.ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        System.Threading.CancellationToken cancellationToken = default)
    {
        Capture(command);
        return new(result);
    }
}

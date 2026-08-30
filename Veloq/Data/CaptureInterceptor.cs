using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Veloq.Data;

public sealed record CapturedCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters)
{
    public int RowsFetched { get; internal set; }
}

public sealed class CaptureInterceptor : DbCommandInterceptor
{
    public List<CapturedCommand> Commands { get; } = [];
    public int QueryCount => Commands.Count;

    public void Reset() => Commands.Clear();

    internal void Capture(DbCommand command)
    {
        List<(string Name, object? Value)> parameters = command.Parameters
            .Cast<DbParameter>()
            .Select(parameter => (parameter.ParameterName, parameter.Value))
            .ToList();

        Commands.Add(new CapturedCommand(command.CommandText, parameters));
    }

    private DbDataReader WrapLatest(DbDataReader reader)
    {
        if (Commands.Count == 0)
        {
            return reader;
        }

        CapturedCommand command = Commands[^1];
        return new CountingDbDataReader(reader, () => command.RowsFetched++);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Capture(command);

        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Capture(command);

        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        return WrapLatest(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new(WrapLatest(result));
    }
}

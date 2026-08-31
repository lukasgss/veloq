using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<CapturedCommand> _commands = new();
    private readonly ConcurrentDictionary<DbCommand, CapturedCommand> _byCommand = new();

    public IReadOnlyList<CapturedCommand> Commands => _commands.ToList();

    public int QueryCount => _commands.Count;

    public void Reset()
    {
        _commands.Clear();
        _byCommand.Clear();
    }

    internal void Capture(DbCommand command)
    {
        List<(string Name, object? Value)> parameters = command.Parameters
            .Cast<DbParameter>()
            .Select(parameter => (parameter.ParameterName, parameter.Value))
            .ToList();

        CapturedCommand captured = new(command.CommandText, parameters);

        _commands.Enqueue(captured);
        _byCommand[command] = captured;
    }

    private DbDataReader Wrap(DbCommand command, DbDataReader reader)
    {
        if (!_byCommand.TryRemove(command, out CapturedCommand? captured))
        {
            return reader;
        }

        return new CountingDbDataReader(reader, () => captured.RowsFetched++);
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
        return Wrap(command, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new(Wrap(command, result));
    }
}

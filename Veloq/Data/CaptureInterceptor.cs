using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Veloq.Data;

public sealed record CapturedCommand(
    string Sql,
    IReadOnlyList<(string Name, object? Value)> Parameters);

/// <summary>Records every SQL command EF executes so diagnostics can show and explain
/// all round-trips, including intentional split queries.</summary>
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

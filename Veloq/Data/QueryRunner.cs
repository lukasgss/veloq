using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Veloq.Data.Schema;

namespace Veloq.Data;

public sealed class QueryRunner
{
    /// <summary>Maximum number of rows rendered into the results grid. The full result is
    /// still enumerated (so <see cref="QueryResult.RowCount"/> stays accurate); this only
    /// caps how many are turned into display strings so a huge result set can't freeze the UI.</summary>
    private const int MaxDisplayRows = 500;

    private readonly string _connectionString;
    private readonly List<MetadataReference> _references;

    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private CompiledModel? _model;

    private static readonly Type[] Types =
    [
        typeof(object),
        typeof(Enumerable),
        typeof(Queryable),
        typeof(List<>),
        typeof(System.Linq.Expressions.Expression),
        typeof(DbContext),
        typeof(DbContextOptions),
        typeof(NpgsqlConnection),
        typeof(RelationalEntityTypeBuilderExtensions),
    ];

    private static readonly string[] Imports =
    [
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "Microsoft.EntityFrameworkCore",
        "Veloq.Data",
        CSharpModelEmitter.Namespace
    ];

    public QueryRunner(string connectionString)
    {
        _connectionString = connectionString;
        _references = BuildReferences();
    }

    private static List<MetadataReference> BuildReferences()
    {
        List<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToList();

        foreach (Type t in Types)
        {
            if (!assemblies.Contains(t.Assembly))
            {
                assemblies.Add(t.Assembly);
            }
        }

        return assemblies
            .Select(a => a.Location)
            .Distinct()
            .Select(loc => (MetadataReference)MetadataReference.CreateFromFile(loc))
            .ToList();
    }

    public async Task<CompiledModel> GetModelAsync()
    {
        if (_model is not null)
        {
            return _model;
        }

        await _modelLock.WaitAsync();
        try
        {
            if (_model is not null)
            {
                return _model;
            }

            DatabaseModel db = await PgSchemaReader.ReadAsync(_connectionString);
            List<MetadataReference> references = [.. _references];
            _model = ModelCompiler.Compile(db, references);

            return _model;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public void InvalidateModel() => _model = null;

    private DbContext CreateContext(CompiledModel model, CaptureInterceptor interceptor)
    {
        DbContextOptions options = new DbContextOptionsBuilder()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

        return (DbContext)Activator.CreateInstance(model.ContextType, options)!;
    }

    private ScriptOptions BuildScriptOptions(CompiledModel model)
    {
        List<MetadataReference> refs = new(_references)
        {
            MetadataReference.CreateFromImage(model.Image),
        };

        return ScriptOptions.Default
            .WithReferences(refs)
            .WithImports(Imports);
    }

    /// <summary>Opens a connection to verify the credentials/host are reachable.</summary>
    public async Task<string> TestConnectionAsync()
    {
        await using NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync();
        return conn.PostgreSqlVersion.ToString();
    }

    /// <summary>Optional: create the sample Customers/Orders schema and seed it into the
    /// connected database (only if the user opts in — we never touch existing data).</summary>
    public Task SeedSampleSchemaAsync() => Task.Run(() =>
    {
        DbContextOptions<ECommerceDbContext> options = new DbContextOptionsBuilder<ECommerceDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        using ECommerceDbContext db = new(options);

        db.EnsureSeeded();
        InvalidateModel();
    });

    public async Task<QueryResult> RunAsync(string expression, string country)
    {
        CaptureInterceptor interceptor = new();

        try
        {
            CompiledModel model = await GetModelAsync();

            await using DbContext db = CreateContext(model, interceptor);

            object host = Activator.CreateInstance(model.HostType)!;
            model.HostType.GetField("db")!.SetValue(host, db);
            model.HostType.GetField("country")!.SetValue(host, country);

            object? value;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // The final line must be an *expression* so Roslyn returns (and auto-awaits
                // a trailing Task<T>) its value. A trailing ';' turns it into a statement,
                // which silently yields null and, for *Async calls, leaves an unawaited task
                // running on a pooled connection.
                value = await CSharpScript.EvaluateAsync<object>(
                    StripTrailingSemicolons(expression),
                    BuildScriptOptions(model),
                    host,
                    model.HostType);

                value = await UnwrapAsync(value);
            }
            catch (CompilationErrorException ex)
            {
                return QueryResult.Fail("Compilation error:\n" + string.Join("\n", ex.Diagnostics));
            }

            var (columns, rows, count) = Materialize(value);
            sw.Stop();

            string sql = interceptor.LastSql ?? "(query executed client-side — no SQL generated)";

            string plan = interceptor.LastSql is null
                ? "(no server-side query to explain)"
                : await ExplainAsync(interceptor);

            return new QueryResult
            {
                Success = true,
                Sql = sql,
                Plan = plan,
                QueryCount = interceptor.QueryCount,
                ElapsedMs = sw.ElapsedMilliseconds,
                Columns = columns,
                Rows = rows,
                RowCount = count,
            };
        }
        catch (Exception ex)
        {
            return QueryResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<object?> UnwrapAsync(object? value)
    {
        switch (value)
        {
            case Task task:
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result")?.GetValue(task);

            case not null when value.GetType() is { IsGenericType: true } vt && vt.GetGenericTypeDefinition() == typeof(ValueTask<>):
                Task asTask = (Task)vt.GetMethod("AsTask")!.Invoke(value, null)!;
                await asTask.ConfigureAwait(false);

                return asTask.GetType().GetProperty("Result")?.GetValue(asTask);

            default:
                return value;
        }
    }

    private static string StripTrailingSemicolons(string expression)
    {
        string trimmed = expression.TrimEnd();

        while (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed;
    }

    private static (List<string>, List<string[]>, int) Materialize(object? value)
    {
        List<string> columns = [];
        List<string[]> rows = [];

        if (value is not IEnumerable enumerable || value is string)
        {
            columns.Add("Value");
            rows.Add([Format(value)]);

            return (columns, rows, 1);
        }

        List<object?> items = enumerable.Cast<object?>().ToList();
        int total = items.Count;
        if (total == 0)
        {
            return (columns, rows, 0);
        }

        Type? elemType = items.First(i => i is not null)?.GetType();
        PropertyInfo[]? props = elemType is null || elemType.IsPrimitive || elemType == typeof(string) || elemType == typeof(decimal)
            ? null
            : elemType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        if (props is null)
        {
            columns.Add("Value");

            foreach (object? item in items.Take(MaxDisplayRows))
            {
                rows.Add([Format(item)]);
            }
        }
        else
        {
            columns.AddRange(props.Select(p => p.Name));

            foreach (object? item in items.Take(MaxDisplayRows))
            {
                rows.Add(props.Select(p => Format(p.GetValue(item))).ToArray());
            }
        }

        return (columns, rows, total);
    }

    /// <summary>Renders a cell value. PostgreSQL <c>timestamptz</c> comes back as a UTC
    /// <see cref="DateTime"/> (Kind=Utc); we convert those to local time and show the
    /// offset so the display matches what psql prints in the session time zone. Plain
    /// <c>timestamp</c> values (Kind=Unspecified) are wall-clock and left untouched.</summary>
    private static string Format(object? value) => value switch
    {
        null => "null",
        DateTime { Kind: DateTimeKind.Utc } d => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:sszzz"),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset o => o.ToLocalTime().ToString("yyyy-MM-dd HH:mm:sszzz"),
        _ => value.ToString() ?? "null",
    };

    private async Task<string> ExplainAsync(CaptureInterceptor interceptor)
    {
        try
        {
            await using NpgsqlConnection conn = new(_connectionString);
            await conn.OpenAsync();

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"EXPLAIN (ANALYZE, VERBOSE, BUFFERS, FORMAT TEXT) {interceptor.LastSql}";

            foreach ((string name, object? val) in interceptor.LastParams)
            {
                cmd.Parameters.Add(new NpgsqlParameter(name, val ?? DBNull.Value));
            }

            StringBuilder sb = new();

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sb.AppendLine(reader.GetString(0));
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(could not obtain query plan: {ex.Message})";
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Veloq.Data;

/// <summary>Compiles a user-supplied LINQ expression with Roslyn, runs it against a real
/// PostgreSQL database, and reports the generated SQL, the query planner output
/// (EXPLAIN ANALYZE) and the materialized rows.</summary>
public sealed class QueryRunner
{
    private readonly string _connectionString;
    private readonly ScriptOptions _scriptOptions;

    public QueryRunner(string connectionString)
    {
        _connectionString = connectionString;

        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToList();
        // Guarantee the assemblies we rely on are present even if not yet JIT-loaded.
        foreach (var t in new[]
                 {
                     typeof(object), typeof(Enumerable), typeof(Queryable),
                     typeof(DbContext), typeof(ECommerceDbContext), typeof(NpgsqlConnection),
                 })
        {
            if (!refs.Contains(t.Assembly)) refs.Add(t.Assembly);
        }

        _scriptOptions = ScriptOptions.Default
            .WithReferences(refs.Distinct())
            .WithImports(
                "System", "System.Linq", "System.Collections.Generic",
                "Microsoft.EntityFrameworkCore", "Veloq.Data");
    }

    private ECommerceDbContext CreateContext(CaptureInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<ECommerceDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .Options;
        return new ECommerceDbContext(options);
    }

    /// <summary>Opens a connection to verify the credentials/host are reachable.</summary>
    public async Task<string> TestConnectionAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn.PostgreSqlVersion.ToString();
    }

    /// <summary>Optional: create the sample Customers/Orders schema and seed it into the
    /// connected database (only if the user opts in — we never touch existing data).</summary>
    public Task SeedSampleSchemaAsync() => Task.Run(() =>
    {
        using var db = CreateContext(new CaptureInterceptor());
        db.EnsureSeeded();
    });

    public async Task<QueryResult> RunAsync(string expression, string country)
    {
        var interceptor = new CaptureInterceptor();
        try
        {
            await using var db = CreateContext(interceptor);
            var globals = new ScriptGlobals { db = db, country = country };

            object? value;
            var sw = Stopwatch.StartNew();
            try
            {
                value = await CSharpScript.EvaluateAsync<object>(expression, _scriptOptions, globals);
            }
            catch (CompilationErrorException ex)
            {
                return QueryResult.Fail("Compilation error:\n" + string.Join("\n", ex.Diagnostics));
            }

            var (columns, rows, count) = Materialize(value);
            sw.Stop();

            var sql = interceptor.LastSql ?? "(query executed client-side — no SQL generated)";
            var plan = interceptor.LastSql is null
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

    private static (List<string>, List<string[]>, int) Materialize(object? value)
    {
        var columns = new List<string>();
        var rows = new List<string[]>();
        if (value is not IEnumerable enumerable || value is string)
        {
            columns.Add("Value");
            rows.Add(new[] { value?.ToString() ?? "null" });
            return (columns, rows, 1);
        }

        var items = enumerable.Cast<object?>().ToList();
        var total = items.Count;
        if (total == 0) return (columns, rows, 0);

        var elemType = items.First(i => i is not null)?.GetType();
        var props = elemType is null || elemType.IsPrimitive || elemType == typeof(string) || elemType == typeof(decimal)
            ? null
            : elemType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        if (props is null)
        {
            columns.Add("Value");
            foreach (var item in items.Take(500))
                rows.Add(new[] { item?.ToString() ?? "null" });
        }
        else
        {
            columns.AddRange(props.Select(p => p.Name));
            foreach (var item in items.Take(500))
                rows.Add(props.Select(p => p.GetValue(item)?.ToString() ?? "null").ToArray());
        }
        return (columns, rows, total);
    }

    private async Task<string> ExplainAsync(CaptureInterceptor interceptor)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN (ANALYZE, VERBOSE, BUFFERS, FORMAT TEXT) " + interceptor.LastSql;
            foreach (var (name, val) in interceptor.LastParams)
                cmd.Parameters.Add(new NpgsqlParameter(name, val ?? DBNull.Value));

            var sb = new StringBuilder();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sb.AppendLine(reader.GetString(0));
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(could not obtain query plan: {ex.Message})";
        }
    }
}

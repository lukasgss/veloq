using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Veloq.Data.Schema;

namespace Veloq.Data;

public sealed class QueryRunner
{
    private readonly string _connectionString;
    private readonly List<MetadataReference> _references;

    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private CompiledModel? _model;
    private ScriptOptions? _scriptOptions;

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
            .Select(MetadataReference (loc) => MetadataReference.CreateFromFile(loc))
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
            _scriptOptions = BuildScriptOptions(_model);

            return _model;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private DbContext CreateContext(CompiledModel model, CaptureInterceptor interceptor, bool noTracking)
    {
        DbContextOptions options = new DbContextOptionsBuilder()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .Options;

        DbContext context = (DbContext)Activator.CreateInstance(model.ContextType, options)!;

        if (noTracking)
        {
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        }

        return context;
    }

    private ScriptOptions GetScriptOptions() =>
        _scriptOptions ?? throw new InvalidOperationException("Model not loaded.");

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

    public async Task<IReadOnlyList<CompletionSuggestion>> GetCompletionsAsync(string expression, int position)
    {
        CompiledModel model = await GetModelAsync();
        ScriptOptions scriptOptions = GetScriptOptions();
        position = Math.Clamp(position, 0, expression.Length);

        return await ComputeCompletionsOffUiThread(expression, position, model, scriptOptions);
    }

    private static async Task<IReadOnlyList<CompletionSuggestion>> ComputeCompletionsOffUiThread(
        string expression,
        int position,
        CompiledModel model,
        ScriptOptions scriptOptions)
    {
        return await Task.Run(() => ComputeCompletions(expression, position, model, scriptOptions));
    }

    private static IReadOnlyList<CompletionSuggestion> ComputeCompletions(
        string expression,
        int position,
        CompiledModel model,
        ScriptOptions scriptOptions)
    {
        Script<object> script = CSharpScript.Create<object>(
            expression,
            scriptOptions,
            model.HostType);

        Compilation compilation = script.GetCompilation();
        SyntaxTree tree = compilation.SyntaxTrees.Last();
        SyntaxNode root = tree.GetRoot();
        SemanticModel semanticModel = compilation.GetSemanticModel(tree);

        int tokenPosition = position == 0 ? 0 : position - 1;
        SyntaxToken token = root.FindToken(tokenPosition, findInsideTrivia: true);
        MemberAccessExpressionSyntax? memberAccess = token.Parent?
            .AncestorsAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault(m => m.OperatorToken.Span.End <= position && m.FullSpan.End >= position);

        IEnumerable<ISymbol> symbols;
        if (memberAccess is not null)
        {
            SymbolInfo expressionSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression);
            ITypeSymbol? container = semanticModel.GetTypeInfo(memberAccess.Expression).Type
                ?? expressionSymbol.Symbol as ITypeSymbol;
            if (container is null)
            {
                return [];
            }

            bool staticAccess = expressionSymbol.Symbol is INamedTypeSymbol;

            symbols = semanticModel
                .LookupSymbols(position, container, includeReducedExtensionMethods: true)
                .Where(s => staticAccess ? s.IsStatic : !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
        }
        else
        {
            symbols = semanticModel.LookupSymbols(position);
        }

        return symbols
            .Where(IsCompletionSymbol)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => ToSuggestion(
                g.First(),
                g.Count(),
                g.OfType<IMethodSymbol>().Any(CanInvokeWithoutArguments)))
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
    }

    private static bool IsCompletionSymbol(ISymbol symbol)
    {
        return !symbol.IsImplicitlyDeclared &&
               !string.IsNullOrWhiteSpace(symbol.Name) &&
               symbol.Name[0] != '<' &&
               symbol.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Event or
                   SymbolKind.Method or SymbolKind.Local or SymbolKind.Parameter or SymbolKind.NamedType
                   or SymbolKind.Namespace;
    }

    private static CompletionSuggestion ToSuggestion(
        ISymbol symbol,
        int overloadCount,
        bool canInvokeWithoutArguments)
    {
        int priority = symbol switch
        {
            IPropertySymbol => 4,
            IFieldSymbol or ILocalSymbol or IParameterSymbol => 3,
            IMethodSymbol => 2,
            _ => 1,
        };

        string description = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (overloadCount > 1)
        {
            description += $" (+{overloadCount - 1} overloads)";
        }

        return new CompletionSuggestion(
            symbol.Name,
            description,
            GetCompletionDetail(symbol),
            GetCompletionKind(symbol),
            canInvokeWithoutArguments,
            priority);
    }

    private static bool CanInvokeWithoutArguments(IMethodSymbol method) =>
        method.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams);

    private static string GetCompletionKind(ISymbol symbol) => symbol switch
    {
        IPropertySymbol { Type: INamedTypeSymbol { Name: "DbSet" } } => "Table",
        IPropertySymbol property when IsGeneratedNavigation(property.Type) => "Navigation",
        IPropertySymbol property when property.ContainingType.ContainingNamespace.ToDisplayString() == CSharpModelEmitter.Namespace => "Column",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        ILocalSymbol or IParameterSymbol => "Variable",
        IMethodSymbol { MethodKind: MethodKind.ReducedExtension } => "Extension",
        IMethodSymbol => "Method",
        IEventSymbol => "Event",
        INamedTypeSymbol => "Type",
        INamespaceSymbol => "Namespace",
        _ => "Symbol",
    };

    private static bool IsGeneratedNavigation(ITypeSymbol type)
    {
        if (type.ContainingNamespace.ToDisplayString() == CSharpModelEmitter.Namespace)
        {
            return true;
        }

        return type is INamedTypeSymbol { IsGenericType: true } named &&
               named.TypeArguments.Any(t => t.ContainingNamespace.ToDisplayString() == CSharpModelEmitter.Namespace);
    }

    private static string GetCompletionDetail(ISymbol symbol) => symbol switch
    {
        IPropertySymbol property => property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IFieldSymbol field => field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        ILocalSymbol local => local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IParameterSymbol parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IMethodSymbol method => method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
        _ => string.Empty,
    };

    public async Task<string> TestConnectionAsync()
    {
        await using NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync();
        return conn.PostgreSqlVersion.ToString();
    }

    public async Task<QueryResult> RunAsync(
        string expression,
        BenchmarkOptions? options = null,
        bool noTracking = false)
    {
        options ??= BenchmarkOptions.Single;

        try
        {
            CompiledModel model = await GetModelAsync();

            // Compile once; invoke the delegate per iteration so we measure execution,
            // not Roslyn compilation, on every run.
            Script<object> script = CSharpScript.Create<object>(
                StripTrailingSemicolons(expression),
                GetScriptOptions(),
                model.HostType);

            ScriptRunner<object> run;
            try
            {
                run = script.CreateDelegate();
            }
            catch (CompilationErrorException ex)
            {
                return QueryResult.Fail("Compilation error:\n" + string.Join("\n", ex.Diagnostics));
            }

            int warmup = options.WarmupCount;
            int measured = options.MeasuredCount;
            List<double> timings = new(measured);
            List<double> warmupTimings = new(warmup);
            double coldMs = 0;

            CaptureInterceptor displayInterceptor = new();
            (List<string> Columns, List<string[]> Rows, int Count) display = ([], [], 0);

            for (int i = 0; i < warmup + measured; i++)
            {
                CaptureInterceptor interceptor = new();
                await using DbContext db = CreateContext(model, interceptor, noTracking);

                object host = Activator.CreateInstance(model.HostType)!;
                model.HostType.GetField("db")!.SetValue(host, db);

                Stopwatch sw = Stopwatch.StartNew();

                object? value = await UnwrapAsync(await run(host));
                (List<string> Columns, List<string[]> Rows, int Count) materialized = ResultMaterializer.Materialize(value);

                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                if (i == 0)
                {
                    coldMs = ms;
                }

                if (i < warmup)
                {
                    warmupTimings.Add(ms);
                }
                else
                {
                    timings.Add(ms);
                }

                // Size the sample once the warmups are done. The cold run is discarded (it
                // measures EF translation and connection setup, not the query) and the
                // fastest of the rest is used, since that is the run closest to steady
                // state. Later warmups still carry JIT and pool settling costs.
                if (options.Adaptive && i == warmup - 1 && warmupTimings.Count > 1)
                {
                    measured = BenchmarkOptions.RunsFor(warmupTimings.Skip(1).Min());
                }

                if (i == warmup + measured - 1)
                {
                    displayInterceptor = interceptor;
                    display = materialized;
                }
            }

            (List<string> columns, List<string[]> rows, int count) = display;

            string sql = QueryDiagnostics.FormatSql(displayInterceptor.Commands);
            string plan = displayInterceptor.Commands.Count == 0
                ? "(no server-side query to explain)"
                : await ExplainAsync(displayInterceptor.Commands);

            BenchmarkResult? benchmark = warmup + measured > 1
                ? BenchmarkResult.From(coldMs, timings, warmup)
                : null;

            return new QueryResult
            {
                Success = true,
                Sql = sql,
                Plan = plan,
                QueryCount = displayInterceptor.QueryCount,
                IsSplitQuery = QueryDiagnostics.ContainsAsSplitQuery(expression),
                ElapsedMs = (long)Math.Round(benchmark?.MedianMs ?? coldMs),
                Benchmark = benchmark,
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

    private async Task<string> ExplainAsync(IReadOnlyList<CapturedCommand> commands)
    {
        try
        {
            await using NpgsqlConnection conn = new(_connectionString);
            await conn.OpenAsync();

            StringBuilder output = new();
            for (int index = 0; index < commands.Count; index++)
            {
                CapturedCommand captured = commands[index];
                await using NpgsqlCommand command = conn.CreateCommand();
                command.CommandText = $"EXPLAIN (ANALYZE, VERBOSE, BUFFERS, FORMAT TEXT) {captured.Sql}";

                foreach ((string name, object? value) in captured.Parameters)
                {
                    command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
                }

                if (index > 0)
                {
                    output.AppendLine().AppendLine();
                }

                if (commands.Count > 1)
                {
                    output.AppendLine($"Query {index + 1} of {commands.Count}");
                    output.AppendLine(new string('─', 24));
                }

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    output.AppendLine(reader.GetString(0));
                }
            }

            return output.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(could not obtain query plan: {ex.Message})";
        }
    }
}

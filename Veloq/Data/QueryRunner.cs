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
    private readonly Func<Task<InitializedState>> _initializeState;
    private readonly Func<Task<string>> _testConnection;
    private readonly Action? _onBuildScriptOptions;

    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private volatile InitializedState? _initializedState;

    private sealed record InitializedState(CompiledModel Model, ScriptOptions ScriptOptions);

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
        : this(
            connectionString,
            () => PgSchemaReader.ReadAsync(connectionString),
            ModelCompiler.Compile,
            () => TestDatabaseConnectionAsync(connectionString))
    {
    }

    internal QueryRunner(
        Func<Task<(CompiledModel Model, ScriptOptions ScriptOptions)>> initializeState,
        Func<Task<string>>? testConnection = null)
    {
        _connectionString = string.Empty;
        _references = [];
        _initializeState = async () =>
        {
            (CompiledModel model, ScriptOptions scriptOptions) = await initializeState().ConfigureAwait(false);
            return new InitializedState(model, scriptOptions);
        };
        _testConnection = testConnection ?? (() => Task.FromResult(string.Empty));
    }

    internal QueryRunner(
        string connectionString,
        Func<Task<DatabaseModel>> readModel,
        Func<DatabaseModel, IReadOnlyList<MetadataReference>, CompiledModel> compileModel,
        Action? onBuildScriptOptions = null)
        : this(connectionString, readModel, compileModel, () => Task.FromResult(string.Empty), onBuildScriptOptions)
    {
    }

    private QueryRunner(
        string connectionString,
        Func<Task<DatabaseModel>> readModel,
        Func<DatabaseModel, IReadOnlyList<MetadataReference>, CompiledModel> compileModel,
        Func<Task<string>> testConnection,
        Action? onBuildScriptOptions = null)
    {
        _connectionString = connectionString;
        _references = BuildReferences();
        _initializeState = () => BuildInitializedStateAsync(readModel, compileModel);
        _testConnection = testConnection;
        _onBuildScriptOptions = onBuildScriptOptions;
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
        InitializedState initializedState = await GetInitializedStateAsync().ConfigureAwait(false);

        return initializedState.Model;
    }

    private async Task<InitializedState> GetInitializedStateAsync()
    {
        if (_initializedState is { } initializedState)
        {
            return initializedState;
        }

        await _modelLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initializedState is { } lockedInitializedState)
            {
                return lockedInitializedState;
            }

            InitializedState newState = await _initializeState().ConfigureAwait(false);
            return _initializedState = newState;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private async Task<InitializedState> BuildInitializedStateAsync(
        Func<Task<DatabaseModel>> readModel,
        Func<DatabaseModel, IReadOnlyList<MetadataReference>, CompiledModel> compileModel)
    {
        DatabaseModel db = await readModel().ConfigureAwait(false);
        List<MetadataReference> references = [.. _references];

        return await Task.Run(() =>
        {
            CompiledModel model = compileModel(db, references);
            ScriptOptions scriptOptions = BuildScriptOptions(model);
            return new InitializedState(model, scriptOptions);
        }).ConfigureAwait(false);
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
        _initializedState?.ScriptOptions ?? throw new InvalidOperationException("Model not loaded.");

    private ScriptOptions BuildScriptOptions(CompiledModel model)
    {
        _onBuildScriptOptions?.Invoke();
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
        InitializedState initializedState = await GetInitializedStateAsync().ConfigureAwait(false);
        CompiledModel model = initializedState.Model;
        ScriptOptions scriptOptions = GetScriptOptions();
        position = Math.Clamp(position, 0, expression.Length);

        return await ComputeCompletionsOffUiThread(expression, position, model, scriptOptions).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CompletionSuggestion>> ComputeCompletionsOffUiThread(
        string expression,
        int position,
        CompiledModel model,
        ScriptOptions scriptOptions)
    {
        return await Task.Run(() => ComputeCompletions(expression, position, model, scriptOptions)).ConfigureAwait(false);
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

    public Task<string> TestConnectionAsync() => _testConnection();

    private static async Task<string> TestDatabaseConnectionAsync(string connectionString)
    {
        await using NpgsqlConnection conn = new(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
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
            InitializedState initializedState = await GetInitializedStateAsync().ConfigureAwait(false);
            CompiledModel model = initializedState.Model;

            // Compile once; invoke the delegate per iteration so we measure execution,
            // not Roslyn compilation, on every run. Roslyn compilation is CPU-bound and
            // must stay off the UI thread, so build the delegate on the thread pool.
            ScriptRunner<object> run;
            try
            {
                run = await Task.Run(() =>
                {
                    Script<object> script = CSharpScript.Create<object>(
                        StripTrailingSemicolons(expression),
                        GetScriptOptions(),
                        model.HostType);
                    return script.CreateDelegate();
                }).ConfigureAwait(false);
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
            MaterializedResult display = new([], [], DisplayRowCount: 0, RootCount: 0);
            Type? rootType = null;

            for (int i = 0; i < warmup + measured; i++)
            {
                CaptureInterceptor interceptor = new();
                await using DbContext db = CreateContext(model, interceptor, noTracking);

                object host = Activator.CreateInstance(model.HostType)!;
                model.HostType.GetField("db")!.SetValue(host, db);

                Stopwatch sw = Stopwatch.StartNew();

                object? value = await UnwrapAsync(await run(host).ConfigureAwait(false)).ConfigureAwait(false);
                MaterializedResult materialized = ResultMaterializer.Materialize(value);

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
                    rootType = RootEntityType(value);
                }
            }

            int rowsFetched = displayInterceptor.Commands.Sum(command => command.RowsFetched);
            bool isSplitQuery = QueryDiagnostics.ContainsAsSplitQuery(expression);
            int collectionIncludes = QueryDiagnostics.CountCollectionIncludes(expression, rootType);

            string sql = QueryDiagnostics.FormatSql(displayInterceptor.Commands);
            string plan = displayInterceptor.Commands.Count == 0
                ? "(no server-side query to explain)"
                : await ExplainAsync(displayInterceptor.Commands).ConfigureAwait(false);

            BenchmarkResult? benchmark = warmup + measured > 1
                ? BenchmarkResult.From(coldMs, timings, warmup)
                : null;

            return new QueryResult
            {
                Success = true,
                Sql = sql,
                Plan = plan,
                QueryCount = displayInterceptor.QueryCount,
                IsSplitQuery = isSplitQuery,
                RowsFetched = rowsFetched,
                ReturnedCount = display.RootCount,
                CollectionIncludeCount = collectionIncludes,
                IsCartesianRisk = QueryDiagnostics.IsCartesianRisk(collectionIncludes, isSplitQuery),
                IsCartesian = QueryDiagnostics.IsCartesianExplosion(collectionIncludes, rowsFetched, display.RootCount, isSplitQuery),
                ElapsedMs = (long)Math.Round(benchmark?.MedianMs ?? coldMs),
                Benchmark = benchmark,
                Columns = display.Columns,
                Rows = display.Rows,
                RowCount = display.DisplayRowCount,
            };
        }
        catch (Exception ex)
        {
            return QueryResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Type? RootEntityType(object? value)
    {
        if (value is string or null)
        {
            return value?.GetType();
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item is not null)
                {
                    return item.GetType();
                }
            }

            return null;
        }

        return value.GetType();
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
            await conn.OpenAsync().ConfigureAwait(false);

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

                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
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

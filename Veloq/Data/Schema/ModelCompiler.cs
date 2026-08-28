using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Veloq.Data.Schema;

public sealed class CompiledModel
{
    public required Assembly Assembly { get; init; }
    public required Type ContextType { get; init; }
    public required Type HostType { get; init; }
    public required byte[] Image { get; init; }
    public required string Source { get; init; }
    public required int TableCount { get; init; }
}

public static class ModelCompiler
{
    private static readonly ConcurrentDictionary<string, Assembly> Loaded = new();

    static ModelCompiler()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveGeneratedAssembly;
    }

    private static Assembly? ResolveGeneratedAssembly(object? sender, ResolveEventArgs args)
    {
        if (Loaded.TryGetValue(args.Name, out Assembly? assembly))
        {
            return assembly;
        }

        return null;
    }

    public static CompiledModel Compile(DatabaseModel model, IReadOnlyList<MetadataReference> references)
    {
        string source = CSharpModelEmitter.Emit(model);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "Veloq.Generated_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));

        using MemoryStream ms = new();
        EmitResult emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            IEnumerable<string> errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());

            throw new InvalidOperationException(
                "Failed to compile the schema model:\n" + string.Join("\n", errors) +
                "\n\n--- generated source ---\n" + source);
        }

        byte[] image = ms.ToArray();
        Assembly assembly = Assembly.Load(image);
        Loaded[assembly.FullName!] = assembly;
        string @namespace = CSharpModelEmitter.Namespace;

        return new CompiledModel
        {
            Assembly = assembly,
            ContextType = assembly.GetType($"{@namespace}.{CSharpModelEmitter.ContextName}", throwOnError: true)!,
            HostType = assembly.GetType($"{@namespace}.{CSharpModelEmitter.HostName}", throwOnError: true)!,
            Image = image,
            Source = source,
            TableCount = model.Tables.Count,
        };
    }
}

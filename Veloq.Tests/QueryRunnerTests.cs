using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using Veloq.Data;
using Veloq.Data.Schema;
using Xunit;

namespace Veloq.Tests;

public sealed class QueryRunnerTests
{
    [Fact]
    public async Task ConcurrentModelRequestsInitializeCompleteStateOnce()
    {
        CompiledModel expectedModel = CreateModel();
        ScriptOptions expectedOptions = ScriptOptions.Default;
        TaskCompletionSource initializationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int initializationCount = 0;

        async Task<(CompiledModel Model, ScriptOptions ScriptOptions)> InitializeAsync()
        {
            Interlocked.Increment(ref initializationCount);
            initializationStarted.SetResult();
            await releaseInitialization.Task;
            return (expectedModel, expectedOptions);
        }

        QueryRunner runner = new(InitializeAsync);
        Task<CompiledModel>[] requests = Enumerable.Range(0, 8)
            .Select(_ => runner.GetModelAsync())
            .ToArray();

        await initializationStarted.Task;
        releaseInitialization.SetResult();

        CompiledModel[] models = await Task.WhenAll(requests);

        Assert.Equal(1, initializationCount);
        Assert.All(models, model => Assert.Same(expectedModel, model));
        Assert.Same(expectedModel, await runner.GetModelAsync());
        Assert.Equal(1, initializationCount);
    }

    [Fact]
    public async Task FailedInitializationDoesNotPublishPartialState()
    {
        CompiledModel expectedModel = CreateModel();
        int initializationCount = 0;

        Task<(CompiledModel Model, ScriptOptions ScriptOptions)> InitializeAsync()
        {
            if (Interlocked.Increment(ref initializationCount) == 1)
            {
                throw new InvalidOperationException("Script options failed.");
            }

            return Task.FromResult((expectedModel, ScriptOptions.Default));
        }

        QueryRunner runner = new(InitializeAsync);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(runner.GetModelAsync);
        Assert.Equal("Script options failed.", exception.Message);

        Assert.Same(expectedModel, await runner.GetModelAsync());
        Assert.Equal(2, initializationCount);
    }

    [Fact]
    public async Task GetCompletionsUsesConnectionStringInitializationPathOffUiHandlerThread()
    {
        int uiHandlerThreadId = 0;
        int compilerThreadId = 0;
        QueryRunner runner = new(
            "Host=unused",
            () => Task.FromResult(new DatabaseModel()),
            (model, references) =>
            {
                compilerThreadId = Environment.CurrentManagedThreadId;
                return ModelCompiler.Compile(model, references);
            });
        Task<IReadOnlyList<CompletionSuggestion>>? completions = null;

        Thread uiHandler = new(() =>
        {
            uiHandlerThreadId = Environment.CurrentManagedThreadId;
            completions = runner.GetCompletionsAsync(string.Empty, 0);
        });
        uiHandler.Start();
        uiHandler.Join();

        await completions!;

        Assert.NotEqual(uiHandlerThreadId, compilerThreadId);
    }

    private static CompiledModel CreateModel() => new()
    {
        Assembly = Assembly.GetExecutingAssembly(),
        ContextType = typeof(object),
        HostType = typeof(object),
        Image = [],
        Source = string.Empty,
        TableCount = 0,
    };
}

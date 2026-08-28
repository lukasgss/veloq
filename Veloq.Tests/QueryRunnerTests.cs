using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Veloq.Data;
using Veloq.Data.Schema;
using Xunit;

namespace Veloq.Tests;

public sealed class QueryRunnerTests
{
    [Fact]
    public async Task GetCompletionsInitializesModelOffUiHandlerThread()
    {
        int uiHandlerThreadId = 0;
        int compilerThreadId = 0;
        QueryRunner runner = new(
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
}

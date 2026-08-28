using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using Veloq.Data;
using Veloq.Data.Schema;
using Veloq.ViewModels;
using Xunit;

namespace Veloq.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task SaveConnectionInitializesModelOffCallingThreadBeforeSelection()
    {
        CompiledModel model = CreateModel();
        TaskCompletionSource initializationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Task> saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ConnectionInfo>? savedConnections = null;
        int initializationCount = 0;
        int initializationThreadId = 0;
        int callingThreadId = 0;

        async Task<(CompiledModel Model, ScriptOptions ScriptOptions)> InitializeAsync()
        {
            Interlocked.Increment(ref initializationCount);
            initializationThreadId = Environment.CurrentManagedThreadId;
            initializationStarted.SetResult();
            await releaseInitialization.Task;
            return (model, ScriptOptions.Default);
        }

        QueryRunner runner = new(InitializeAsync, () => Task.FromResult("16.1"));
        MainViewModel viewModel = CreateViewModel(runner, connections => savedConnections = connections.ToList());

        Thread callingThread = new(() =>
        {
            callingThreadId = Environment.CurrentManagedThreadId;
            Task saveTask = viewModel.SaveConnectionCommand.ExecuteAsync(null);
            saveStarted.SetResult(saveTask);
            saveTask.GetAwaiter().GetResult();
        });
        callingThread.Start();

        Task save = await saveStarted.Task;
        await initializationStarted.Task;

        try
        {
            Assert.NotEqual(callingThreadId, initializationThreadId);
            Assert.Null(viewModel.SelectedConnection);
            Assert.Null(savedConnections);
        }
        finally
        {
            releaseInitialization.SetResult();
            await save;
            callingThread.Join();
        }

        ConnectionInfo selected = Assert.IsType<ConnectionInfo>(viewModel.SelectedConnection);
        Assert.Same(runner, selected.Runner);
        Assert.Single(viewModel.Connections);
        Assert.Single(savedConnections!);
        Assert.Equal(1, initializationCount);
    }

    [Fact]
    public async Task SaveConnectionDoesNotPersistOrSelectWhenModelInitializationFails()
    {
        Task<(CompiledModel Model, ScriptOptions ScriptOptions)> InitializeAsync() =>
            Task.FromException<(CompiledModel, ScriptOptions)>(
                new InvalidOperationException("Schema compilation failed."));

        bool saveCalled = false;
        QueryRunner runner = new(InitializeAsync, () => Task.FromResult("16.1"));
        MainViewModel viewModel = CreateViewModel(runner, _ => saveCalled = true);

        await viewModel.SaveConnectionCommand.ExecuteAsync(null);

        Assert.True(viewModel.ConnectionFailed);
        Assert.Equal("InvalidOperationException: Schema compilation failed.", viewModel.ConnectionStatus);
        Assert.False(saveCalled);
        Assert.Empty(viewModel.Connections);
        Assert.Null(viewModel.SelectedConnection);
    }

    private static MainViewModel CreateViewModel(
        QueryRunner runner,
        Action<IEnumerable<ConnectionInfo>> saveConnections) =>
        new(() => [], _ => runner, saveConnections);

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

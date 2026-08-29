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

    [Fact]
    public async Task SelectedConnectionPrefetchFailureIsReported()
    {
        Task<(CompiledModel Model, ScriptOptions ScriptOptions)> InitializeAsync() =>
            Task.FromException<(CompiledModel, ScriptOptions)>(
                new InvalidOperationException("Schema prefetch failed."));

        TaskCompletionSource errorReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueryRunner runner = new(InitializeAsync);
        MainViewModel viewModel = new(
            () => [],
            _ => runner,
            _ => { },
            action =>
            {
                action();
                errorReported.SetResult();
                return Task.CompletedTask;
            });

        viewModel.SelectedConnection = CreateConnection("Test", runner);

        await errorReported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.HasError);
        Assert.Equal("InvalidOperationException: Schema prefetch failed.", viewModel.StatusText);
    }

    [Fact]
    public async Task StalePrefetchFailureDoesNotOverwriteNewSelectionStatus()
    {
        TaskCompletionSource firstPrefetchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstPrefetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failureDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectionInfo first = CreateConnection("First");
        ConnectionInfo second = CreateConnection("Second");

        MainViewModel viewModel = new(
            () => [],
            connection =>
            {
                if (!ReferenceEquals(connection, first))
                {
                    return Task.CompletedTask;
                }

                firstPrefetchStarted.TrySetResult();
                return firstPrefetch.Task;
            },
            action =>
            {
                action();
                failureDispatched.TrySetResult();
                return Task.CompletedTask;
            })
        {
            SelectedConnection = first
        };
        await firstPrefetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.SelectedConnection = second;
        string selectedSecondStatus = viewModel.StatusText;
        Assert.False(viewModel.HasError);

        firstPrefetch.SetException(new InvalidOperationException("Schema prefetch failed."));
        await failureDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(viewModel.HasError);
        Assert.Equal(selectedSecondStatus, viewModel.StatusText);
    }

    private static MainViewModel CreateViewModel(
        QueryRunner runner,
        Action<IEnumerable<ConnectionInfo>> saveConnections) =>
        new(() => [], _ => runner, saveConnections);

    private static ConnectionInfo CreateConnection(string name, QueryRunner? runner = null) => new()
    {
        Name = name,
        Host = "localhost",
        Port = "5432",
        Database = "test",
        Username = "test",
        Password = string.Empty,
        Runner = runner ?? new QueryRunner(string.Empty),
    };

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

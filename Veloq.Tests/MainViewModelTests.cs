using System;
using System.Threading.Tasks;
using Veloq.Data;
using Veloq.ViewModels;
using Xunit;

namespace Veloq.Tests;

public sealed class MainViewModelTests
{
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

    private static ConnectionInfo CreateConnection(string name) => new()
    {
        Name = name,
        Host = "localhost",
        Port = "5432",
        Database = "test",
        Username = "test",
        Password = string.Empty,
        Runner = new QueryRunner(string.Empty),
    };
}

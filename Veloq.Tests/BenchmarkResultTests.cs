using Veloq.Data;
using Xunit;

namespace Veloq.Tests;

public sealed class BenchmarkResultTests
{
    [Fact]
    public void ComputesStatsOverMeasuredRuns()
    {
        double[] measured = [10, 20, 30, 40, 50];

        BenchmarkResult result = BenchmarkResult.From(coldMs: 100, measured, warmupCount: 1);

        Assert.Equal(5, result.MeasuredCount);
        Assert.Equal(1, result.WarmupCount);
        Assert.Equal(100, result.ColdMs);
        Assert.Equal(10, result.MinMs);
        Assert.Equal(50, result.MaxMs);
        Assert.Equal(30, result.MedianMs);
        Assert.Equal(30, result.MeanMs);
        Assert.Equal(48, result.P95Ms);
    }

    [Fact]
    public void IgnoresOrderOfInput()
    {
        double[] shuffled = [50, 10, 40, 20, 30];

        BenchmarkResult result = BenchmarkResult.From(coldMs: 0, shuffled, warmupCount: 0);

        Assert.Equal(10, result.MinMs);
        Assert.Equal(30, result.MedianMs);
        Assert.Equal(50, result.MaxMs);
    }

    [Fact]
    public void SingleMeasuredRunHasZeroStdDev()
    {
        BenchmarkResult result = BenchmarkResult.From(coldMs: 5, [42], warmupCount: 1);

        Assert.Equal(42, result.MedianMs);
        Assert.Equal(42, result.P95Ms);
        Assert.Equal(0, result.StdDevMs);
    }

    [Fact]
    public void EmptyMeasuredFallsBackToCold()
    {
        BenchmarkResult result = BenchmarkResult.From(coldMs: 7, [], warmupCount: 1);

        Assert.Equal(7, result.ColdMs);
        Assert.Equal(7, result.MedianMs);
        Assert.Equal(0, result.MeasuredCount);
    }
}

public sealed class BenchmarkOptionsTests
{
    [Theory]
    [InlineData(0.5, 100)]
    [InlineData(9.9, 100)]
    [InlineData(10, 50)]
    [InlineData(99.9, 50)]
    [InlineData(100, 20)]
    [InlineData(5000, 20)]
    public void ScalesRunCountToQueryCost(double warmMs, int expected)
    {
        Assert.Equal(expected, BenchmarkOptions.RunsFor(warmMs));
    }

    [Fact]
    public void DefaultIsAdaptiveAndSingleIsNot()
    {
        Assert.True(BenchmarkOptions.Default.Adaptive);
        Assert.False(BenchmarkOptions.Single.Adaptive);
        Assert.Equal(1, BenchmarkOptions.Single.Total);
    }

    [Fact]
    public void AdaptiveDefaultWarmsUpEnoughToDiscardTheColdRun()
    {
        Assert.True(BenchmarkOptions.Default.WarmupCount >= 2);
    }
}

using System;
using System.Linq;

namespace Veloq.Data;

public sealed record BenchmarkOptions(int WarmupCount, int MeasuredCount, bool Adaptive = false)
{
    public static readonly BenchmarkOptions Single = new(0, 1);
    public static readonly BenchmarkOptions Default = new(3, 20, Adaptive: true);

    public int Total => WarmupCount + MeasuredCount;

    /// <summary>
    /// How many runs to measure, given what the first warm run cost. Cheap queries get more
    /// samples so the percentiles settle down; expensive ones get fewer so the benchmark
    /// still finishes promptly. Must not be fed the cold run, which is dominated by one-off
    /// EF translation and connection setup rather than the query itself.
    /// </summary>
    public static int RunsFor(double warmMs) => warmMs switch
    {
        < 10 => 100,
        < 100 => 50,
        _ => 20,
    };
}

public sealed record BenchmarkResult(
    int WarmupCount,
    int MeasuredCount,
    double ColdMs,
    double MinMs,
    double MedianMs,
    double P95Ms,
    double MaxMs,
    double MeanMs,
    double StdDevMs)
{
    public static BenchmarkResult From(double coldMs, IReadOnlyList<double> measured, int warmupCount)
    {
        if (measured.Count == 0)
        {
            return new BenchmarkResult(warmupCount, 0, coldMs, coldMs, coldMs, coldMs, coldMs, coldMs, 0);
        }

        double[] sorted = measured.OrderBy(x => x).ToArray();
        double mean = sorted.Average();
        double variance = sorted.Sum(x => (x - mean) * (x - mean)) / sorted.Length;

        return new BenchmarkResult(
            warmupCount,
            sorted.Length,
            coldMs,
            sorted[0],
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            sorted[^1],
            mean,
            Math.Sqrt(variance));
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double rank = q * (sorted.Length - 1);
        int low = (int)Math.Floor(rank);
        int high = (int)Math.Ceiling(rank);
        if (low == high)
        {
            return sorted[low];
        }

        double weight = rank - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }
}

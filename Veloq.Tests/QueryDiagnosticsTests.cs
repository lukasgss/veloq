using System.Collections.Generic;
using Veloq.Data;
using Xunit;

namespace Veloq.Tests;

public sealed class AsNoTrackingRewriteTests
{
    [Theory]
    [InlineData("db.Plan.AsNoTracking().ToListAsync()", true)]
    [InlineData("db.Plan.Include(p => p.TimelineItems).AsNoTracking().ToListAsync()", true)]
    [InlineData("db.Plan.ToListAsync()", false)]
    [InlineData("db.Plan.AsSplitQuery().ToListAsync()", false)]
    public void DetectsAsNoTracking(string expression, bool expected)
    {
        Assert.Equal(expected, QueryDiagnostics.ContainsAsNoTracking(expression));
    }

    [Theory]
    [InlineData("db.Plan.AsNoTracking().ToListAsync()", "db.Plan.ToListAsync()")]
    [InlineData("db.Plan.AsNoTracking()", "db.Plan")]
    [InlineData(
        "db.Plan.Include(p => p.TimelineItems).AsNoTracking().ToListAsync()",
        "db.Plan.Include(p => p.TimelineItems).ToListAsync()")]
    [InlineData("db.Plan.ToListAsync()", "db.Plan.ToListAsync()")]
    public void RemovesAsNoTracking(string expression, string expected)
    {
        Assert.Equal(expected, QueryDiagnostics.RemoveAsNoTracking(expression));
    }

    [Fact]
    public void RemovalLeavesOtherOperatorsAlone()
    {
        string stripped = QueryDiagnostics.RemoveAsNoTracking(
            "db.Plan.AsNoTracking().AsSplitQuery().Where(p => p.Id > 1).ToListAsync()");

        Assert.Equal("db.Plan.AsSplitQuery().Where(p => p.Id > 1).ToListAsync()", stripped);
        Assert.False(QueryDiagnostics.ContainsAsNoTracking(stripped));
    }
}

public sealed class CartesianDetectionTests
{
    [Theory]
    [InlineData("db.Book.ToListAsync()", 0)]
    [InlineData("db.Book.Include(b => b.Reviews).ToListAsync()", 1)]
    [InlineData("db.Book.Include(b => b.Reviews).Include(b => b.Tags).ToListAsync()", 2)]
    [InlineData("db.Book.Include(b => b.Reviews).ThenInclude(r => r.Author).ToListAsync()", 2)]
    public void CountsIncludes(string expression, int expected)
    {
        Assert.Equal(expected, QueryDiagnostics.CountIncludes(expression));
    }

    private sealed class Root
    {
        public List<Review> Reviews { get; } = [];
        public List<object> Tags { get; } = [];
        public object? Author { get; set; }
    }

    private sealed class Review
    {
        public List<object> Comments { get; } = [];
        public object? Author { get; set; }
    }

    [Theory]
    [InlineData("db.Book.ToListAsync()", 0)]
    [InlineData("db.Book.Include(b => b.Reviews).ToListAsync()", 1)]
    [InlineData("db.Book.Include(b => b.Reviews).Include(b => b.Tags).ToListAsync()", 2)]
    [InlineData("db.Book.Include(b => b.Author).ToListAsync()", 0)] // reference nav, not a collection
    [InlineData("db.Book.Include(b => b.Reviews).Include(b => b.Author).ToListAsync()", 1)]
    [InlineData("db.Book.Include(b => b.Reviews.Where(r => r.Author != null)).ToListAsync()", 1)] // filtered direct collection
    [InlineData("db.Book.Include(b => b.Reviews.Where(r => true).OrderBy(r => r.Author)).ToListAsync()", 1)] // chained filters
    [InlineData("db.Book.Include(\"Reviews.Comments\").ToListAsync()", 1)] // nested collection path
    [InlineData("db.Book.Include(\"Reviews.Author\").ToListAsync()", 1)] // path traverses a collection
    [InlineData("db.Book.Include(\"Author\").ToListAsync()", 0)] // reference-only string path
    public void CountsCollectionIncludes(string expression, int expected)
    {
        Assert.Equal(expected, QueryDiagnostics.CountCollectionIncludes(expression, typeof(Root)));
    }

    [Fact]
    public void FlagsTwoCollectionIncludesWithFanOut()
    {
        bool cartesian = QueryDiagnostics.IsCartesianExplosion(
            collectionIncludeCount: 2,
            rowsFetched: 12_400,
            rowsReturned: 200,
            isSplitQuery: false);

        Assert.True(cartesian);
    }

    [Fact]
    public void RiskWithoutFanOutIsStillFlagged()
    {
        Assert.True(QueryDiagnostics.IsCartesianRisk(collectionIncludeCount: 2, isSplitQuery: false));
        Assert.False(QueryDiagnostics.IsCartesianExplosion(
            collectionIncludeCount: 2, rowsFetched: 20, rowsReturned: 20, isSplitQuery: false));
    }

    [Fact]
    public void IgnoresSingleCollectionInclude()
    {
        Assert.False(QueryDiagnostics.IsCartesianRisk(collectionIncludeCount: 1, isSplitQuery: false));
        Assert.False(QueryDiagnostics.IsCartesianExplosion(
            collectionIncludeCount: 1,
            rowsFetched: 12_400,
            rowsReturned: 200,
            isSplitQuery: false));
    }

    [Fact]
    public void IgnoresSplitQuery()
    {
        Assert.False(QueryDiagnostics.IsCartesianRisk(collectionIncludeCount: 2, isSplitQuery: true));
        Assert.False(QueryDiagnostics.IsCartesianExplosion(
            collectionIncludeCount: 2,
            rowsFetched: 12_400,
            rowsReturned: 200,
            isSplitQuery: true));
    }

    [Theory]
    [InlineData(
        "db.Book.Include(b => b.Reviews).Include(b => b.Tags).ToListAsync()",
        "db.Book.Include(b => b.Reviews).Include(b => b.Tags).AsSplitQuery().ToListAsync()")]
    [InlineData("db.Book", "db.Book.AsSplitQuery()")]
    public void AddsAsSplitQueryBeforeTerminal(string expression, string expected)
    {
        Assert.Equal(expected, QueryDiagnostics.AddAsSplitQuery(expression));
    }

    [Fact]
    public void AddedSplitQueryIsDetected()
    {
        string split = QueryDiagnostics.AddAsSplitQuery(
            "db.Book.Include(b => b.Reviews).Include(b => b.Tags).ToListAsync()");

        Assert.True(QueryDiagnostics.ContainsAsSplitQuery(split));
    }

    [Fact]
    public void IgnoresLowFanOut()
    {
        bool cartesian = QueryDiagnostics.IsCartesianExplosion(
            collectionIncludeCount: 2,
            rowsFetched: 250,
            rowsReturned: 200,
            isSplitQuery: false);

        Assert.False(cartesian);
    }
}

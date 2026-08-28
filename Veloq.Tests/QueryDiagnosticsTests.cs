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

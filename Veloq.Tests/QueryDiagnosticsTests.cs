using Npgsql;
using Veloq.Data;
using Xunit;

namespace Veloq.Tests;

public sealed class QueryDiagnosticsTests
{
    [Fact]
    public void CapturesAndFormatsEveryExecutedCommand()
    {
        CaptureInterceptor interceptor = new();
        using NpgsqlCommand first = new("SELECT * FROM plan");
        using NpgsqlCommand second = new("SELECT * FROM timeline WHERE plan_id = @id");
        second.Parameters.AddWithValue("id", 1);

        interceptor.Capture(first);
        interceptor.Capture(second);
        string sql = QueryDiagnostics.FormatSql(interceptor.Commands);

        Assert.Equal(2, interceptor.QueryCount);
        Assert.Contains("-- Query 1 of 2", sql);
        Assert.Contains("SELECT * FROM plan", sql);
        Assert.Contains("-- Query 2 of 2", sql);
        Assert.Contains("SELECT * FROM timeline", sql);
        Assert.Equal(1, interceptor.Commands[1].Parameters[0].Value);
    }

    [Theory]
    [InlineData("db.Plan.AsSplitQuery().ToListAsync()", true)]
    [InlineData("db.Plan.ToListAsync()", false)]
    [InlineData("db.Plan.Where(x => x.Name == \"AsSplitQuery\").ToListAsync()", false)]
    public void DetectsExplicitSplitQuerySyntax(string expression, bool expected)
    {
        Assert.Equal(expected, QueryDiagnostics.ContainsAsSplitQuery(expression));
    }
}

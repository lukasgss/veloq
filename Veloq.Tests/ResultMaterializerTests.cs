using Veloq.Data;
using Xunit;

namespace Veloq.Tests;

public sealed class ResultMaterializerTests
{
    [Fact]
    public void ScalarEntityIsRenderedAsSinglePropertyRow()
    {
        var plan = new Plan { Id = 1, Name = "Basic" };

        var (columns, rows, count) = ResultMaterializer.Materialize(plan);

        Assert.Equal(["Id", "Name"], columns);
        Assert.Single(rows);
        Assert.Equal(["1", "Basic"], rows[0]);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ScalarPrimitiveKeepsValueColumn()
    {
        var (columns, rows, count) = ResultMaterializer.Materialize(42);

        Assert.Equal(["Value"], columns);
        Assert.Single(rows);
        Assert.Equal(["42"], rows[0]);
        Assert.Equal(1, count);
    }

    private sealed class Plan
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}

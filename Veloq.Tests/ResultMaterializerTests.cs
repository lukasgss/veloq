using System.Collections.Generic;
using Veloq.Data;
using Xunit;

namespace Veloq.Tests;

public sealed class ResultMaterializerTests
{
    [Fact]
    public void ScalarEntityIsRenderedAsSinglePropertyRow()
    {
        var plan = new ScalarPlan { Id = 1, Name = "Basic" };

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

    [Fact]
    public void IncludedCollectionExpandsIntoPrefixedColumns()
    {
        Plan plan = new() { Id = 1, Name = "Basic" };
        plan.TimelineItems.Add(new Timeline { Id = 10, CustomUrl = "alice-bob", PlanId = 1, Plan = plan });
        plan.TimelineItems.Add(new Timeline { Id = 11, CustomUrl = "vasco-clara", PlanId = 1, Plan = plan });
        Plan premium = new() { Id = 2, Name = "Premium" };

        var (columns, rows, count) = ResultMaterializer.Materialize(new[] { plan, premium });

        Assert.DoesNotContain("TimelineItems", columns);
        Assert.Contains("TimelineItems.Id", columns);
        Assert.Contains("TimelineItems.CustomUrl", columns);
        Assert.Equal(3, rows.Count);
        Assert.Equal("10", rows[0][columns.IndexOf("TimelineItems.Id")]);
        Assert.Equal("alice-bob", rows[0][columns.IndexOf("TimelineItems.CustomUrl")]);
        Assert.Equal("11", rows[1][columns.IndexOf("TimelineItems.Id")]);
        Assert.Equal("vasco-clara", rows[1][columns.IndexOf("TimelineItems.CustomUrl")]);
        Assert.Equal("1", rows[0][columns.IndexOf("Id")]);
        Assert.Equal("1", rows[1][columns.IndexOf("Id")]);
        Assert.Equal("2", rows[2][columns.IndexOf("Id")]);
        Assert.Equal("null", rows[2][columns.IndexOf("TimelineItems.Id")]);
        Assert.Equal(3, count);
    }

    [Fact]
    public void IncludedReferenceDoesNotRenderInverseNavigationGraph()
    {
        Plan plan = new() { Id = 1, Name = "Basic" };
        Timeline current = new() { Id = 10, PlanId = 1, Plan = plan };
        plan.TimelineItems.Add(current);
        plan.TimelineItems.Add(new Timeline { Id = 11, PlanId = 1, Plan = plan });

        var (columns, rows, _) = ResultMaterializer.Materialize(current);

        Assert.DoesNotContain("Plan", columns);
        Assert.DoesNotContain("MemoryItems", columns);
        Assert.DoesNotContain("Plan.TimelineItems", columns);
        Assert.Contains("Plan.Id", columns);
        Assert.Contains("Plan.Name", columns);
        Assert.Equal("1", rows[0][columns.IndexOf("Plan.Id")]);
        Assert.Equal("Basic", rows[0][columns.IndexOf("Plan.Name")]);
    }

    private sealed class ScalarPlan
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class Timeline
    {
        public int Id { get; init; }
        public string CustomUrl { get; init; } = string.Empty;
        public int PlanId { get; init; }
        public Plan? Plan { get; init; }
        public List<int> MemoryItems { get; } = [];
    }

    private sealed class Plan
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public List<Timeline> TimelineItems { get; } = [];
    }
}

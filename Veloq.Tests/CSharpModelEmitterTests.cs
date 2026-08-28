using Veloq.Data.Schema;
using Xunit;

namespace Veloq.Tests;

public sealed class CSharpModelEmitterTests
{
    [Fact]
    public void IdColumnInfersNavigationToMatchingTable()
    {
        DatabaseModel model = BuildColumnOnlyPlanModel();

        string source = CSharpModelEmitter.Emit(model);

        Assert.Contains("public PlanType? PlanType { get; set; }", source);
        Assert.Contains("public List<Plan> PlanItems { get; } = new();", source);
        Assert.Contains(".HasOne(x => x.PlanType)", source);
        Assert.Contains(".HasForeignKey(x => x.PlanTypeId);", source);
    }

    [Fact]
    public void UnmatchedIdColumnDoesNotInventNavigation()
    {
        DatabaseModel model = BuildColumnOnlyPlanModel();
        model.Tables[1].Columns.Add(new ColumnModel
        {
            Name = "OwnerId",
            UdtName = "int4",
            IsNullable = false,
        });

        string source = CSharpModelEmitter.Emit(model);

        Assert.DoesNotContain("public Owner? Owner", source);
    }

    private static DatabaseModel BuildColumnOnlyPlanModel()
    {
        DatabaseModel model = new();
        TableModel planType = new() { Schema = "public", Name = "PlanType" };
        planType.Columns.Add(new ColumnModel
        {
            Name = "Id",
            UdtName = "int4",
            IsNullable = false,
            IsPrimaryKey = true,
        });
        planType.Columns.Add(new ColumnModel
        {
            Name = "Label",
            UdtName = "text",
            IsNullable = false,
        });

        TableModel plan = new() { Schema = "public", Name = "Plan" };
        plan.Columns.Add(new ColumnModel
        {
            Name = "Id",
            UdtName = "int4",
            IsNullable = false,
            IsPrimaryKey = true,
        });
        plan.Columns.Add(new ColumnModel
        {
            Name = "PlanTypeId",
            UdtName = "int4",
            IsNullable = false,
        });

        model.Tables.Add(planType);
        model.Tables.Add(plan);
        return model;
    }
}

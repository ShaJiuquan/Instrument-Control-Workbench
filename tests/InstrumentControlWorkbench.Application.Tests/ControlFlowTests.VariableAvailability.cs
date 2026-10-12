using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task VariableInspectionExcludesFutureDefinitionsAndPreservesTypesWithoutConnecting()
    {
        var b = new Bench(); var plan = new ExperimentPlan("variables", new SequenceStep(new("root"), [
            new AssignValueStep(new("number"), new("n"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
            new AssignValueStep(new("flag"), new("ready"), new BooleanValue(true)),
            new DelayStep(new("inspect"), TimeSpan.Zero),
            new AssignValueStep(new("future"), new("later"), new TextValue("future"))]));
        var before = b.Runs.DescribeVariables(plan, new("inspect"));
        Assert.Equal(new[] { "n", "ready" }, before.Select(v => v.Id.Value)); Assert.Equal(BlackboardValueType.Boolean, before[1].Type);
        Assert.Equal(new[] { new StepId("number") }, before[0].PossibleDefinitions); Assert.All(before, variable => Assert.False(variable.IsActiveAxis));
        var append = b.Runs.DescribeVariables(plan, new("root"), PlanVariablePosition.AppendChild); Assert.Equal(3, append.Count); Assert.Equal(BlackboardValueType.Text, append.Single(v => v.Id.Value == "later").Type);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
        Assert.Throws<NotSupportedException>(() => ((IList<PlanVariableAvailability>)before).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StepId>)before[0].PossibleDefinitions).Clear());
    }

    [Fact]
    public void VariableInspectionJoinsOnlyEveryPathDefinitionsAndReportsBothBranchWriters()
    {
        var b = new Bench(); var plan = new ExperimentPlan("branch", Seed(new SequenceStep(new("body"), [
            new IfElseStep(new("if"), new(Flag, ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
                new SequenceStep(new("then"), [new AssignValueStep(new("then-write"), new("common"), new ConstantValue(new(1, PhysicalUnit.Volt))), new AssignValueStep(new("only-write"), new("only_then"), new BooleanValue(true))]),
                new AssignValueStep(new("else-write"), new("common"), new ConstantValue(new(2, PhysicalUnit.Volt)))),
            new DelayStep(new("inspect"), TimeSpan.Zero)])));
        var before = b.Runs.DescribeVariables(plan, new("inspect"));
        Assert.DoesNotContain(before, v => v.Id.Value == "only_then"); var common = before.Single(v => v.Id.Value == "common");
        Assert.Equal(PhysicalUnit.Volt, common.Type.Unit); Assert.Equal(new[] { "else-write", "then-write" }, common.PossibleDefinitions.Select(id => id.Value));
        var inElse = b.Runs.DescribeVariables(plan, new("else-write")); Assert.DoesNotContain(inElse, v => v.Id.Value is "common" or "only_then");
    }

    [Fact]
    public void SubtreeInspectionUsesMappedLocalTypesAndAppendPositionBeforeOutputCopy()
    {
        var b = new Bench(); var subtree = new SubtreeStep(new("scope"), [new DelayStep(new("local-point"), TimeSpan.Zero), new AssignValueStep(new("local-write"), new("private"), new TextValue("local"))],
            [new(new("outer"), new("input"))], [new(new("private"), new("exported"))]);
        var plan = new ExperimentPlan("scope", new SequenceStep(new("root"), [new AssignValueStep(new("outer-write"), new("outer"), new BooleanValue(true)), subtree, new DelayStep(new("after"), TimeSpan.Zero)]));
        var inside = b.Runs.DescribeVariables(plan, new("local-point")); Assert.Equal(BlackboardValueType.Boolean, inside.Single(v => v.Id.Value == "input").Type); Assert.Equal("scope", Assert.Single(inside.Single(v => v.Id.Value == "input").PossibleDefinitions).Value);
        var append = b.Runs.DescribeVariables(plan, new("scope"), PlanVariablePosition.AppendChild); Assert.Contains(append, v => v.Id.Value == "private"); Assert.DoesNotContain(append, v => v.Id.Value == "exported");
        var after = b.Runs.DescribeVariables(plan, new("after")); Assert.DoesNotContain(after, v => v.Id.Value is "private" or "input"); Assert.Equal("scope", Assert.Single(after.Single(v => v.Id.Value == "exported").PossibleDefinitions).Value);
    }

    [Fact]
    public void IteratorBodyContainsValueIndexAndTotalButOuterEntryDoesNot()
    {
        var b = new Bench(); var plan = new ExperimentPlan("iterator", new ForEachStep(new("loop"), new("n"), new ScanValues([new(1, PhysicalUnit.Kelvin), new(2, PhysicalUnit.Kelvin)]), new SequenceStep(new("body"), [new DelayStep(new("point"), TimeSpan.Zero)])));
        Assert.Empty(b.Runs.DescribeVariables(plan, new("loop")));
        var inside = b.Runs.DescribeVariables(plan, new("point")); Assert.Equal(3, inside.Count); Assert.All(inside, v => { Assert.True(v.IsActiveAxis); Assert.Equal("loop", Assert.Single(v.PossibleDefinitions).Value); }); Assert.Equal(PhysicalUnit.Kelvin, inside.Single(v => v.Id.Value == "n").Type.Unit);
    }

    [Fact]
    public void ReadColumnsBecomeAvailableOnlyAfterTheReadAndHaveReadStepDefinition()
    {
        var b = new Bench(); var plan = new ExperimentPlan("read", new SequenceStep(new("root"), [b.ReadField("read"), new DelayStep(new("after"), TimeSpan.Zero)]));
        Assert.Empty(b.Runs.DescribeVariables(plan, new("read"))); var after = b.Runs.DescribeVariables(plan, new("after"));
        var read = Assert.Single(after); Assert.Equal("read.value", read.Id.Value); Assert.Equal(PhysicalUnit.Tesla, read.Type.Unit); Assert.Equal("read", Assert.Single(read.PossibleDefinitions).Value);
    }

    [Fact]
    public void DecoratorInspectionPreservesTheActualChildDefinitionRatherThanSyntheticJoinWriter()
    {
        var b = new Bench(); var plan = new ExperimentPlan("decorated read", new SequenceStep(new("root"), [new ResultDecoratorStep(new("wrapper"), ResultEffect.ForceSuccess, b.ReadField("read")), new DelayStep(new("after"), TimeSpan.Zero)]));
        var read = Assert.Single(b.Runs.DescribeVariables(plan, new("after"))); Assert.Equal("read", Assert.Single(read.PossibleDefinitions).Value);
    }

    [Theory]
    [InlineData("missing", PlanVariablePosition.BeforeStep)]
    [InlineData("point", PlanVariablePosition.AppendChild)]
    public void MissingOrNonContainerInspectionPositionRejects(string id, PlanVariablePosition position)
    {
        var b = new Bench(); var plan = new ExperimentPlan("invalid inspection", new DelayStep(new("point"), TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => b.Runs.DescribeVariables(plan, new(id), position));
        Assert.Throws<ArgumentNullException>(() => b.Runs.DescribeVariables(plan, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Runs.DescribeVariables(plan, new("point"), (PlanVariablePosition)99));
    }

    [Fact]
    public void InspectionStillValidatesLaterUndefinedReferencesAndCannotBypassPreflight()
    {
        var b = new Bench(); var plan = new ExperimentPlan("invalid plan", new SequenceStep(new("root"), [new DelayStep(new("point"), TimeSpan.Zero), new AssignValueStep(new("bad"), new("result"), new VariableValue(new("undefined")))]));
        Assert.Throws<ArgumentException>(() => b.Runs.DescribeVariables(plan, new("point")));
    }
}

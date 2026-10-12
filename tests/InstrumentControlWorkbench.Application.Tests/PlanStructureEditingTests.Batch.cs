using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PlanStructureEditingTests
{
    [Fact]
    public void BatchReadPreservesRequestedOrderAndBatchReplacementPreservesTheWholeEnvelope()
    {
        var codec = new JsonExperimentPlanDocument(); var plan = BatchFixture(); var before = codec.Serialize(plan);
        var selected = codec.ReadSteps(plan, [new("b"), new("a")]); Assert.Equal(new[] { "b", "a" }, selected.Select(step => step.Id.Value)); Assert.Equal(TimeSpan.FromSeconds(2), ((DelayStep)selected[0]).Duration);
        var updated = codec.ReplaceSteps(plan, [new DelayStep(new("a"), TimeSpan.FromSeconds(3)), new DelayStep(new("b"), TimeSpan.FromSeconds(4))]);
        var root = (SequenceStep)updated.Root; Assert.Equal(new[] { "a", "b" }, root.Children.Select(step => step.Id.Value)); Assert.Equal(TimeSpan.FromSeconds(3), ((DelayStep)root.Children[0]).Duration); Assert.Equal(TimeSpan.FromSeconds(4), ((DelayStep)root.Children[1]).Duration);
        Assert.Equal(plan.Name, updated.Name); Assert.Equal(plan.MaximumStepExecutions, updated.MaximumStepExecutions); Assert.Equal(plan.Metadata, updated.Metadata); Assert.Equal(before, codec.Serialize(plan));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(updated)), JsonNode.Parse(codec.Serialize(codec.Parse(codec.Serialize(updated))))));
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("overlap")]
    public void InvalidBatchCannotApplyOnlySomeReplacements(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var plan = BatchFixture(); var before = codec.Serialize(plan);
        ExperimentStep[] replacements = damage switch
        {
            "empty" => [],
            "duplicate" => [new DelayStep(new("a"), TimeSpan.Zero), new DelayStep(new("a"), TimeSpan.Zero)],
            "unknown" => [new DelayStep(new("a"), TimeSpan.Zero), new DelayStep(new("missing"), TimeSpan.Zero)],
            _ => [new SequenceStep(new("root"), []), new DelayStep(new("a"), TimeSpan.Zero)],
        };
        Assert.ThrowsAny<ArgumentException>(() => codec.ReplaceSteps(plan, replacements)); Assert.Equal(before, codec.Serialize(plan));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void BatchReadRejectsDuplicateOrUnknownIdentifiers(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var plan = BatchFixture(); Assert.ThrowsAny<ArgumentException>(() => codec.ReadSteps(plan, [new("a"), new(damage == "duplicate" ? "a" : "missing")]));
    }
    [Fact]
    public void BatchReplacementStillProtectsRequiredMeasurementAcquisitionSlots()
    {
        var codec = new JsonExperimentPlanDocument(); var first = new ReadStep(new("first"), [new(new("a"), new(new("source"), new("current")))]); var second = new ReadStep(new("second"), [new(new("b"), new(new("source"), new("voltage")))]);
        var plan = new ExperimentPlan("required", new MeasurementGroupStep(new("point"), [new("First", first), new("Second", second)])); var before = codec.Serialize(plan);
        Assert.ThrowsAny<Exception>(() => codec.ReplaceSteps(plan, [new DelayStep(first.Id, TimeSpan.Zero), new DelayStep(second.Id, TimeSpan.Zero)])); Assert.Equal(before, codec.Serialize(plan));
    }
    private static ExperimentPlan BatchFixture() => new("batch", new SequenceStep(new("root"), [new DelayStep(new("a"), TimeSpan.FromSeconds(1)), new DelayStep(new("b"), TimeSpan.FromSeconds(2))]), metadata: new Dictionary<string, InstrumentControlWorkbench.Core.Parameters.Quantity> { ["fixture"] = new(7, InstrumentControlWorkbench.Core.Parameters.PhysicalUnit.Dimensionless) }, maximumStepExecutions: 12);
}

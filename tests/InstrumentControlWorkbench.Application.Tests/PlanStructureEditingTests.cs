using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PlanStructureEditingTests
{
    [Fact]
    public async Task EditsPreserveEnvelopeAndActualOrderWithoutAnyDeviceConnection()
    {
        var codec = new JsonExperimentPlanDocument(); var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub();
        var runs = new MeasurementRunService(new InstrumentManager(source, hub), new(TimeSpan.FromSeconds(5)), hub); var workspace = new ExperimentPlanWorkspace(codec, runs);
        var plan = new ExperimentPlan("editable", new SequenceStep(new("root"), [new DelayStep(new("a"), TimeSpan.Zero), new DelayStep(new("b"), TimeSpan.Zero)]),
            metadata: new Dictionary<string, Quantity> { ["temperature"] = new(300, PhysicalUnit.Kelvin) }, maximumStepExecutions: 100);
        var appended = workspace.EditStructure(plan, new(PlanStructureEditKind.Append, plan.Root.Id, "{\"id\":\"c\",\"kind\":\"delay\",\"seconds\":0}"));
        var moved = workspace.EditStructure(appended.Plan, new(PlanStructureEditKind.MoveEarlier, new("c")));
        Assert.Equal(new[] { "a", "c", "b" }, ((SequenceStep)moved.Plan.Root).Children.Select(s => s.Id.Value));
        var removed = workspace.EditStructure(moved.Plan, new(PlanStructureEditKind.Remove, new("a")));
        Assert.Equal(new[] { "c", "b" }, ((SequenceStep)removed.Plan.Root).Children.Select(s => s.Id.Value));
        Assert.Equal(plan.Metadata, removed.Plan.Metadata); Assert.Equal(100, removed.Plan.MaximumStepExecutions); Assert.Equal(plan.Name, removed.Plan.Name);
        Assert.Equal(2, ((SequenceStep)plan.Root).Children.Count); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        await runs.StartPlanAsync(removed.Plan, CancellationToken.None); Assert.False(runs.HasActiveRun);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("wrongId")]
    [InlineData("unit")]
    [InlineData("unsupportedField")]
    [InlineData("negativeDelay")]
    [InlineData("budget")]
    public async Task MalformedOrSemanticallyInvalidMutationKeepsOriginalPlanAndNeverConnects(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var runs = new MeasurementRunService(new InstrumentManager(source, hub), new(TimeSpan.FromSeconds(5)), hub);
        var workspace = new ExperimentPlanWorkspace(codec, runs); var plan = new ExperimentPlan("original", new SequenceStep(new("root"), [new DelayStep(new("a"), TimeSpan.Zero)]), maximumStepExecutions: 2);
        var original = codec.Serialize(plan);
        var fragment = damage switch
        {
            "duplicate" => "{\"id\":\"a\",\"kind\":\"sequence\",\"children\":[{\"id\":\"root\",\"kind\":\"delay\",\"seconds\":0}]}",
            "wrongId" => "{\"id\":\"other\",\"kind\":\"delay\",\"seconds\":0}",
            "unit" => "{\"id\":\"a\",\"kind\":\"assign\",\"variable\":\"x\",\"value\":{\"value\":1,\"unit\":\"bad-unit\"}}",
            "unsupportedField" => "{\"id\":\"a\",\"kind\":\"delay\",\"seconds\":0,\"silent\":true}",
            "negativeDelay" => "{\"id\":\"a\",\"kind\":\"delay\",\"seconds\":-1}",
            "budget" => "{\"id\":\"a\",\"kind\":\"repeat\",\"cycles\":3,\"body\":{\"id\":\"inside\",\"kind\":\"delay\",\"seconds\":0}}",
            _ => "{\"id\":\"missing\",\"kind\":\"delay\",\"seconds\":0}",
        };
        Assert.ThrowsAny<Exception>(() => workspace.EditStructure(plan, new(PlanStructureEditKind.Replace, new(damage == "unknown" ? "missing" : "a"), fragment)));
        Assert.Equal(original, codec.Serialize(plan)); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Fact]
    public void RequiredBodyAndRootCannotBeRemovedAndArrayBoundariesAreEnforced()
    {
        var codec = new JsonExperimentPlanDocument(); var plan = new ExperimentPlan("required", new SequenceStep(new("root"), [new TimeoutStep(new("deadline"), TimeSpan.FromSeconds(1), new DelayStep(new("body"), TimeSpan.Zero))]));
        foreach (var selected in new[] { "root", "body" }) Assert.Throws<InvalidOperationException>(() => codec.EditStructure(plan, new(PlanStructureEditKind.Remove, new(selected))));
        Assert.Throws<InvalidOperationException>(() => codec.EditStructure(plan, new(PlanStructureEditKind.MoveEarlier, new("deadline"))));
        Assert.Throws<InvalidOperationException>(() => codec.EditStructure(plan, new(PlanStructureEditKind.Append, new("deadline"), "{\"id\":\"extra\",\"kind\":\"delay\",\"seconds\":0}")));
        var nodes = codec.DescribeStructure(plan); Assert.Equal(new[] { "root", "deadline", "body" }, nodes.Select(n => n.Id.Value));
        Assert.False(nodes[2].CanRemove); Assert.True(nodes[1].CanRemove); Assert.Equal(new StepId("deadline"), nodes[2].Parent);
    }
    [Fact]
    public void RemovingVariableProducerIsRejectedBeforeReplacingTheValidatedPlan()
    {
        var codec = new JsonExperimentPlanDocument(); var hub = new NotificationHub(); var runs = new MeasurementRunService(new InstrumentManager(new MockKeithley2450SourceMeter(), hub), new(TimeSpan.FromSeconds(5)), hub);
        var workspace = new ExperimentPlanWorkspace(codec, runs);
        var plan = new ExperimentPlan("variables", new SequenceStep(new("root"), [new AssignValueStep(new("producer"), new("x"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
            new ConditionStep(new("consumer"), new(new("x"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))))]));
        var text = codec.Serialize(plan); Assert.ThrowsAny<Exception>(() => workspace.EditStructure(plan, new(PlanStructureEditKind.Remove, new("producer"))));
        Assert.Equal(text, codec.Serialize(plan)); Assert.Equal(2, ((SequenceStep)workspace.Inspect(plan).Plan.Root).Children.Count);
    }
    [Theory]
    [InlineData("repeat")]
    [InlineData("retry")]
    [InlineData("timeout")]
    [InlineData("subtree")]
    [InlineData("fallback")]
    [InlineData("while")]
    [InlineData("outcome")]
    [InlineData("switch")]
    [InlineData("hook")]
    [InlineData("decorator")]
    [InlineData("ifElse")]
    [InlineData("scan")]
    [InlineData("forEach")]
    public void NativeStructureUsesActualNestedSlotsAndCanReplaceTheirLeafWithoutLosingOtherNodes(string kind)
    {
        var codec = new JsonExperimentPlanDocument(); ExperimentStep leaf = new DelayStep(new("leaf"), TimeSpan.Zero);
        var condition = new ValueCondition(new("x"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless)));
        ExperimentStep root = kind switch
        {
            "repeat" => new RepeatStep(new("root"), 2, leaf),
            "retry" => new RetryStep(new("root"), 2, TimeSpan.Zero, leaf),
            "timeout" => new TimeoutStep(new("root"), TimeSpan.FromSeconds(1), leaf),
            "subtree" => new SubtreeStep(new("root"), [leaf]),
            "fallback" => new FallbackStep(new("root"), [leaf]),
            "while" => new WhileStep(new("root"), condition, leaf),
            "outcome" => new BranchOnOutcomeStep(new("root"), new ConditionStep(new("condition"), condition), leaf, new SequenceStep(new("otherwise"), [])),
            "switch" => new ThresholdSwitchStep(new("root"), new("x"), [leaf], [new(0, PhysicalUnit.Dimensionless)]),
            "hook" => new OutcomeMutationStep(new("root"), leaf, new(new("success"), new("x"), VariableMutation.Set, new(1, PhysicalUnit.Dimensionless))),
            "ifElse" => new IfElseStep(new("root"), condition, leaf, new SequenceStep(new("otherwise"), [])),
            "scan" => new ScanStep(new("root"), new(new("source"), new("voltage")), new("axis"), new([new(0, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt)]), TimeSpan.Zero, leaf),
            "forEach" => new ForEachStep(new("root"), new("index"), new([new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless)]), leaf),
            _ => new ResultDecoratorStep(new("root"), ResultEffect.ForceSuccess, leaf),
        };
        var plan = new ExperimentPlan("slots", root); var before = codec.DescribeStructure(plan);
        Assert.Equal(new StepId("root"), Assert.Single(before, n => n.Id.Value == "leaf").Parent);
        var updated = codec.EditStructure(plan, new(PlanStructureEditKind.Replace, new("leaf"), "{\"id\":\"leaf\",\"kind\":\"delay\",\"seconds\":0.01}"));
        Assert.Equal(before.Select(n => n.Id), codec.DescribeStructure(updated).Select(n => n.Id));
        Assert.Equal(.01, JsonNode.Parse(codec.ReadFragment(updated, new("leaf")))!["seconds"]!.GetValue<double>());
    }
    [Theory]
    [InlineData("field-voltage.plan.json", "field_stable")]
    [InlineData("sample-four-wire.plan.json", "sample_read")]
    [InlineData("sample-hall-analysis.plan.json", "hall_read_1")]
    public void FragmentReplacementRetainsPreparationsRoutingAndMetadata(string file, string selected)
    {
        var codec = new JsonExperimentPlanDocument();
        // Test runner cwd is not the repo: resolve from the copied test assembly.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples/plans/field-voltage.plan.json"))) directory = directory.Parent;
        if (directory is null) throw new IOException("Repository example not found.");
        var plan = codec.Parse(File.ReadAllText(Path.Combine(directory.FullName, "examples/plans", file)));
        var before = JsonNode.Parse(codec.Serialize(plan))!; var fragment = codec.ReadFragment(plan, new(selected));
        var edited = codec.EditStructure(plan, new(PlanStructureEditKind.Replace, new(selected), fragment)); var after = JsonNode.Parse(codec.Serialize(edited))!;
        Assert.True(JsonNode.DeepEquals(before, after)); Assert.Equal(plan.Preparations.Count, edited.Preparations.Count);
    }
    [Fact]
    public void ReplacingOneMeasurementMemberPreservesTheOtherMemberAndRequiredAcquisition()
    {
        var codec = new JsonExperimentPlanDocument();
        var first = new ReadStep(new("first"), [new(new("a"), new(new("source"), new("current")))]);
        var second = new ReadStep(new("second"), [new(new("b"), new(new("source"), new("voltage")))]);
        var plan = new ExperimentPlan("members", new MeasurementGroupStep(new("point"), [new("A", first), new("B", second)]));
        var nodes = codec.DescribeStructure(plan); Assert.Equal(new[] { "point", "first", "second" }, nodes.Select(n => n.Id.Value));
        Assert.False(nodes[1].CanRemove); Assert.False(nodes[2].CanRemove);
        var fragment = codec.ReadFragment(plan, first.Id);
        var edited = codec.EditStructure(plan, new(PlanStructureEditKind.Replace, first.Id, fragment));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(plan)), JsonNode.Parse(codec.Serialize(edited))));
        Assert.ThrowsAny<Exception>(() => codec.EditStructure(plan, new(PlanStructureEditKind.Replace, first.Id, "{\"id\":\"first\",\"kind\":\"delay\",\"seconds\":0}")));
        Assert.Equal(second.Id, ((MeasurementGroupStep)edited.Root).Members[1].Body.Id);
    }
    [Fact]
    public void StructureEditingRetainsTheNativeCodecDepthBudget()
    {
        var codec = new JsonExperimentPlanDocument(); ExperimentStep root = new DelayStep(new("deep-leaf"), TimeSpan.Zero);
        for (var depth = 0; depth < 70; depth++) root = new SequenceStep(new("level-" + depth), [root]);
        var plan = new ExperimentPlan("deep", root); Assert.Equal(71, codec.DescribeStructure(plan).Count);
        var fragment = codec.ReadFragment(plan, new("deep-leaf"));
        var edited = codec.EditStructure(plan, new(PlanStructureEditKind.Replace, new("deep-leaf"), fragment));
        Assert.Equal(codec.Serialize(plan), codec.Serialize(edited));
    }
}

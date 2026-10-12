using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PlanTypedEditingTests
{
    [Fact]
    public async Task TypedInstrumentEditSavesReloadsAndExecutesWithOriginalPreparationsAndMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "typed-plan-" + Guid.NewGuid().ToString("N") + ".json");
        var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var manager = new InstrumentManager(source, hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub); var codec = new JsonExperimentPlanDocument(); var workspace = new ExperimentPlanWorkspace(codec, runs);
        var address = new ParameterAddress(manager.InstrumentId, Keithley2450Parameters.SourceVoltage.Id);
        var plan = new ExperimentPlan("typed-source", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), manager.InstrumentId, true),
            new SetParameterStep(new("set"), address, new ConstantValue(new(.01, PhysicalUnit.Volt))),
            new ReadStep(new("read"), [new(new("current"), new(manager.InstrumentId, Keithley2450Parameters.MeasuredCurrent.Id))])]),
            [new(manager.InstrumentId, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.000105, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))],
            metadata: new Dictionary<string, Quantity> { ["fixture"] = new(7, PhysicalUnit.Dimensionless) });
        try
        {
            var edited = workspace.EditStep(plan, PlanStructureEditKind.Replace, new("set"), new SetParameterStep(new("set"), address, new ConstantValue(new(.02, PhysicalUnit.Volt))));
            await workspace.SaveAsync(path, edited.Text, CancellationToken.None); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
            var freshSource = new MockKeithley2450SourceMeter(); var freshHub = new NotificationHub(); var freshRuns = new MeasurementRunService(new InstrumentManager(freshSource, freshHub), new(TimeSpan.FromSeconds(5)), freshHub);
            var loaded = await new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), freshRuns).LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(edited.Text), JsonNode.Parse(loaded.Text))); Assert.Single(loaded.Plan.Preparations); Assert.Equal(7, loaded.Plan.Metadata["fixture"].Value);
            await freshRuns.StartPlanAsync(loaded.Plan, CancellationToken.None); Assert.Single(freshRuns.LatestDataSet!.Rows);
            Assert.Equal(PhysicalUnit.Ampere, freshRuns.LatestDataSet.Rows[0].Values[new("current")].Unit);
            Assert.False((await freshSource.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(freshRuns.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("field-voltage.plan.json", "field_stable")]
    [InlineData("sample-four-wire.plan.json", "sample_read")]
    [InlineData("sample-hall-analysis.plan.json", "hall_read_1")]
    public void TypedNodeReadAndReplacementPreserveTheCompleteNativeEnvelope(string file, string selected)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples/plans", file))) directory = directory.Parent;
        Assert.NotNull(directory); var codec = new JsonExperimentPlanDocument(); var plan = codec.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "examples/plans", file)));
        var step = codec.ReadStep(plan, new(selected)); Assert.Equal(selected, step.Id.Value);
        var changed = codec.EditStep(plan, PlanStructureEditKind.Replace, step.Id, step);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(plan)), JsonNode.Parse(codec.Serialize(changed))));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.EditStep(plan, PlanStructureEditKind.Remove, step.Id, step));
    }
    [Fact]
    public async Task TypedAppendAndReplacementUseTheRealCompilerWithoutConnectingAndExecuteTheEditedOrder()
    {
        var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var runs = new MeasurementRunService(new InstrumentManager(source, hub), new(TimeSpan.FromSeconds(5)), hub);
        var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
        var plan = new ExperimentPlan("typed", new SequenceStep(new("root"), [new DelayStep(new("a"), TimeSpan.Zero)]), metadata: new Dictionary<string, Quantity> { ["tag"] = new(1, PhysicalUnit.Dimensionless) });
        var next = workspace.EditStep(plan, PlanStructureEditKind.Append, plan.Root.Id, new DelayStep(new("b"), TimeSpan.Zero));
        var replaced = workspace.EditStep(next.Plan, PlanStructureEditKind.Replace, new("a"), new RepeatStep(new("a"), 2, new DelayStep(new("inside"), TimeSpan.Zero)));
        Assert.Equal(2, ((SequenceStep)replaced.Plan.Root).Children.Count); Assert.Equal(plan.Metadata, replaced.Plan.Metadata); Assert.Single(workspace.Devices);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected); await runs.StartPlanAsync(replaced.Plan, CancellationToken.None); Assert.False(runs.HasActiveRun);
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("renamed")]
    [InlineData("missingVariable")]
    [InlineData("budget")]
    public async Task InvalidTypedCandidatePreservesOriginalPlanAndPerformsNoIO(string kind)
    {
        var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var runs = new MeasurementRunService(new InstrumentManager(source, hub), new(TimeSpan.FromSeconds(5)), hub);
        var codec = new JsonExperimentPlanDocument(); var workspace = new ExperimentPlanWorkspace(codec, runs);
        var plan = new ExperimentPlan("unchanged", new SequenceStep(new("root"), [new DelayStep(new("a"), TimeSpan.Zero)]), maximumStepExecutions: 3); var before = codec.Serialize(plan);
        ExperimentStep replacement = kind switch
        {
            "duplicate" => new SequenceStep(new("a"), [new DelayStep(new("root"), TimeSpan.Zero)]),
            "renamed" => new DelayStep(new("other"), TimeSpan.Zero),
            "missingVariable" => new ConditionStep(new("a"), new(new("missing"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless)))),
            _ => new RepeatStep(new("a"), 10, new DelayStep(new("inside"), TimeSpan.Zero)),
        };
        Assert.ThrowsAny<Exception>(() => workspace.EditStep(plan, PlanStructureEditKind.Replace, new("a"), replacement)); Assert.Equal(before, codec.Serialize(plan));
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
}

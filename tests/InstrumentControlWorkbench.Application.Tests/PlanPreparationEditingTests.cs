using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PlanPreparationEditingTests
{
    [Theory]
    [InlineData("power")]
    [InlineData("fixedSource")]
    [InlineData("fixedSense")]
    [InlineData("unknown")]
    [InlineData("wrongFamily")]
    [InlineData("duplicateFamily")]
    public async Task InvalidPreparationIsRejectedByTheWholeCompilerWithoutConnectionOrMutation(string damage)
    {
        var b = new Bench(); var before = b.Codec.Serialize(b.Plan); var id = new InstrumentId(damage is "wrongFamily" or "duplicateFamily" ? "current" : damage == "unknown" ? "missing" : "k2450");
        var level = damage == "power" ? 30 : damage == "fixedSource" ? .03 : .01;
        var prep = new SourcePreparation(id, SourceFunction.Voltage, new(level, PhysicalUnit.Volt), new(damage == "power" ? .2 : .0001, PhysicalUnit.Ampere),
            new(SenseFunction.Current, SensingMode.FourWire, 1, damage != "fixedSense", damage == "fixedSense" ? new Quantity(1e-8, PhysicalUnit.Ampere) : null),
            damage == "fixedSource" ? new Quantity(.02, PhysicalUnit.Volt) : null);
        var plan = damage == "wrongFamily" ? new ExperimentPlan("empty", new DelayStep(new("delay"), TimeSpan.Zero)) : b.Plan;
        Assert.ThrowsAny<Exception>(() => b.Workspace.UpdateSourcePreparation(plan, prep)); Assert.Equal(before, b.Codec.Serialize(b.Plan)); await b.AssertDisconnected();
    }
    [Fact]
    public async Task RemovingARequiredPreparationAndChangingModeAreRejectedBeforeAnyIO()
    {
        var b = new Bench(); var plan = new ExperimentPlan("paired", new ReadResistanceStep(new("read"), new("k2450"), SourceFunction.Voltage, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere)), b.Plan.Preparations);
        b.Workspace.Inspect(plan); var before = b.Codec.Serialize(plan);
        Assert.Throws<ArgumentException>(() => b.Workspace.RemovePreparation(plan, new("k2450")));
        Assert.Throws<ArgumentException>(() => b.Workspace.UpdateSourcePreparation(plan, new(new("k2450"), SourceFunction.Current, new(.00001, PhysicalUnit.Ampere), new(1, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.FourWire, 1, true))));
        Assert.Equal(before, b.Codec.Serialize(plan)); await b.AssertDisconnected();
    }
    [Fact]
    public async Task SourceAndCurrentPreparationEditsSaveReloadAndExecuteOnFreshSimulatedDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), "prepared-plan-" + Guid.NewGuid().ToString("N") + ".json"); var b = new Bench();
        var root = new SequenceStep(new("root"), [new SourceOutputStep(new("smu-on"), new("k2450"), true), new SourceOutputStep(new("current-on"), new("current"), true),
            new ReadStep(new("read"), [new(new("smu_level"), new(new("k2450"), Keithley2450Parameters.SourceVoltage.Id)), new(new("dc_level"), new(new("current"), Keithley6221Parameters.Current.Id))])]);
        var plan = new ExperimentPlan("two prepared sources", root, b.Plan.Preparations, b.Plan.Metadata, currentSourcePreparations: b.Plan.CurrentSourcePreparations, maximumStepExecutions: 80);
        var smu = new SourcePreparation(new("k2450"), SourceFunction.Voltage, new(.02, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere),
            new(SenseFunction.Current, SensingMode.TwoWire, .5, false, new(.0001, PhysicalUnit.Ampere), new(false, true, true, 37, AveragingMode.Moving)), new(.2, PhysicalUnit.Volt));
        var current = new CurrentSourcePreparation(new("current"), new(new(.00002, PhysicalUnit.Ampere), new(12, PhysicalUnit.Volt), new(.002, PhysicalUnit.Ampere), new(.007, PhysicalUnit.Second)));
        try
        {
            var doc = b.Workspace.UpdateSourcePreparation(plan, smu); doc = b.Workspace.UpdateCurrentSourcePreparation(doc.Plan, current);
            Assert.Same(root, doc.Plan.Root); Assert.Equal(plan.Metadata, doc.Plan.Metadata); Assert.Equal(80, doc.Plan.MaximumStepExecutions);
            await b.Workspace.SaveAsync(path, doc.Text, CancellationToken.None); await b.AssertDisconnected();
            var fresh = new Bench(); var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(doc.Text), JsonNode.Parse(loaded.Text))); Assert.Equal(smu, Assert.Single(loaded.Plan.Preparations)); Assert.Equal(current, Assert.Single(loaded.Plan.CurrentSourcePreparations));
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows);
            Assert.Equal(new Quantity(.02, PhysicalUnit.Volt), row.Values[new("smu_level")]); Assert.Equal(new Quantity(.00002, PhysicalUnit.Ampere), row.Values[new("dc_level")]);
            var smuState = await fresh.Source.GetSnapshotAsync(CancellationToken.None); var currentState = await fresh.Current.GetSnapshotAsync(CancellationToken.None);
            Assert.False(smuState.OutputEnabled); Assert.False(currentState.OutputEnabled); Assert.Equal(smu.Sense, smuState.Sense); Assert.Equal(current.Settings, currentState.Settings); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task UnusedPreparationRemovalPreservesOtherFamilyOrderAndRejectsAnAbsentDevice()
    {
        var b = new Bench(); var removed = b.Workspace.RemovePreparation(b.Plan, new("k2450"));
        Assert.Empty(removed.Plan.Preparations); Assert.Equal(b.Plan.CurrentSourcePreparations, removed.Plan.CurrentSourcePreparations); Assert.Same(b.Plan.Root, removed.Plan.Root);
        var restored = b.Workspace.UpdateSourcePreparation(removed.Plan, b.Plan.Preparations[0]); Assert.Equal(b.Plan.Preparations, restored.Plan.Preparations);
        Assert.Throws<ArgumentException>(() => b.Workspace.RemovePreparation(restored.Plan, new("missing"))); await b.AssertDisconnected();
    }
    [Fact]
    public async Task CurrentPreparationCannotTargetASourceMeterOrUnregisteredDevice()
    {
        var b = new Bench(); var empty = new ExperimentPlan("empty", new DelayStep(new("delay"), TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => b.Workspace.UpdateCurrentSourcePreparation(empty, new(new("k2450"), b.Plan.CurrentSourcePreparations[0].Settings)));
        Assert.ThrowsAny<Exception>(() => b.Workspace.UpdateCurrentSourcePreparation(empty, new(new("missing"), b.Plan.CurrentSourcePreparations[0].Settings))); await b.AssertDisconnected();
    }
    [Fact]
    public async Task ActualRoutedSamplePlanRetainsItsFrozenRoutingAndNestedScanAfterPreparationEdit()
    {
        var codec = new JsonExperimentPlanDocument();
        var plan = await codec.LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "sample-four-wire.plan.json"), CancellationToken.None);
        var hub = new NotificationHub(); var source = new MockKeithley2450SourceMeter(); var manager = new InstrumentManager(source, hub);
        var matrix = new MockMatrixSwitch(new(new(new("matrix"), "K3706", "Simulation matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 6)], [source.Id]));
        var registry = new DeviceRegistry([manager, new MatrixSwitchSession(matrix, hub)], new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub); var workspace = new ExperimentPlanWorkspace(codec, runs);
        var original = plan.Preparations[0];
        var edited = workspace.UpdateSourcePreparation(plan, new(original.Device, original.Function, original.Level, new(.002, PhysicalUnit.Ampere), original.Sense, original.ManualSourceRange));
        Assert.Same(plan.Root, edited.Plan.Root); Assert.Same(plan.Routing, edited.Plan.Routing); Assert.Equal(plan.Metadata, edited.Plan.Metadata); Assert.Equal(plan.MaximumStepExecutions, edited.Plan.MaximumStepExecutions);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(plan))!["routing"], JsonNode.Parse(edited.Text)!["routing"]));
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await matrix.GetSnapshotAsync(CancellationToken.None)).Connected);
        await runs.StartPlanAsync(edited.Plan, CancellationToken.None); Assert.Equal(3, runs.LatestDataSet!.Rows.Count);
        Assert.All(runs.LatestDataSet.Rows, row => edited.Plan.Routing!.ValidateContext(row.Routing!));
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.Empty((await matrix.GetSnapshotAsync(CancellationToken.None)).Closed);
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockKeithley6221CurrentSource Current { get; } = new(new("current"), "Simulated DC source");
        public JsonExperimentPlanDocument Codec { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public ExperimentPlan Plan { get; }
        public Bench()
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub);
            var registry = new DeviceRegistry([manager, new Keithley6221CurrentSourceSession(Current, hub)], new(TimeSpan.FromSeconds(5)));
            Runs = new(registry, hub); Workspace = new(Codec, Runs);
            Plan = new("preparations", new DelayStep(new("delay"), TimeSpan.Zero), [new(new("k2450"), SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))],
                metadata: new Dictionary<string, Quantity> { ["fixture"] = new(7, PhysicalUnit.Dimensionless) }, currentSourcePreparations: [new(new("current"), new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))], maximumStepExecutions: 80);
        }
        public async Task AssertDisconnected()
        {
            Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Current.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False(Runs.HasActiveRun);
        }
    }
}

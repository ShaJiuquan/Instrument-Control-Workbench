using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PlanSampleCatalogEditingTests
{
    [Theory]
    [InlineData("unknownWiring")]
    [InlineData("unguardedSource")]
    [InlineData("wrongMeterRole")]
    [InlineData("outsideInactiveDirection")]
    [InlineData("duplicateTerminal")]
    [InlineData("duplicateId")]
    [InlineData("catalogLimit")]
    public async Task NewSampleValidatesEveryDirectionRuntimeRoleAndCatalogLimitWithoutIO(string damage)
    {
        var b = new Bench(); var original = b.Plan.Routing!.Samples[0]; var before = b.Codec.Serialize(b.Plan); var electrodes = original.Electrodes.ToArray();
        if (damage == "duplicateTerminal") electrodes[1] = new(electrodes[1].Label, electrodes[0].Channel, electrodes[0].Connection);
        if (damage == "outsideInactiveDirection") electrodes[^1] = new(electrodes[^1].Label, electrodes[^1].Channel, new("outside"));
        var created = new SampleProfile(new(damage == "duplicateId" ? original.Id.Value.ToUpperInvariant() : "created"), "Created", original.Topology, electrodes, original.Geometry);
        var plan = b.Plan;
        if (damage == "catalogLimit")
        {
            var samples = new[] { original }.Concat(Enumerable.Range(0, 63).Select(i => new SampleProfile(new("other" + i), "Other", original.Topology, original.Electrodes)));
            plan = new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, new(plan.Routing!.Matrix, plan.Routing.Channels, plan.Routing.Wirings, samples), plan.CurrentSourcePreparations, plan.MaximumStepExecutions);
        }
        var wire = damage switch { "unknownWiring" => "missing", "unguardedSource" => "unguarded", "wrongMeterRole" => "wrong-meter", _ => "smu_kelvin" };
        Assert.NotNull(Record.Exception(() => b.Workspace.AddSample(plan, created, new(wire)))); Assert.Equal(before, b.Codec.Serialize(b.Plan)); await b.AssertNoIO();
    }
    [Theory]
    [InlineData("used")]
    [InlineData("last")]
    [InlineData("missing")]
    public async Task RemovalCannotDropAReferencedLastOrMissingSample(string damage)
    {
        var b = new Bench(); var original = b.Plan.Routing!.Samples[0]; var plan = b.Plan;
        if (damage == "used") plan = new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, new(plan.Routing.Matrix, plan.Routing.Channels, plan.Routing.Wirings, [original, new(new("unused"), "Unused", original.Topology, original.Electrodes)]), plan.CurrentSourcePreparations, plan.MaximumStepExecutions);
        var before = b.Codec.Serialize(plan); Assert.NotNull(Record.Exception(() => b.Workspace.RemoveSample(plan, new(damage == "missing" ? "absent" : original.Id.Value)))); Assert.Equal(before, b.Codec.Serialize(plan)); await b.AssertNoIO();
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockKeithley2450SourceMeter Other { get; } = new(new("other"), "Other");
        public MockMatrixSwitch Matrix { get; }
        public JsonExperimentPlanDocument Codec { get; } = new();
        public ExperimentPlan Plan { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench()
        {
            var original = Codec.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plans/sample-hall-analysis.plan.json"))); var routing = original.Routing!; var wire = routing.Wirings[0]; var channel = routing.Channels[0];
            var extended = new ChannelProfile(channel.Id, channel.Name, channel.Connections.Append(new(new("outside"), 1, 99)));
            var unguarded = new InstrumentWiringProfile(new("unguarded"), Other.Id, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus);
            var wrongMeter = new InstrumentWiringProfile(new("wrong-meter"), Source.Id, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus, voltageMeter: Other.Id);
            Plan = new(original.Name, original.Root, original.Preparations, original.Metadata, new(routing.Matrix, [extended], [wire, unguarded, wrongMeter], routing.Samples), original.CurrentSourcePreparations, original.MaximumStepExecutions);
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); var other = new InstrumentManager(Other, hub);
            Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 12)], [Source.Id]));
            var runs = new MeasurementRunService(new DeviceRegistry([manager, other, new MatrixSwitchSession(Matrix, hub)], new(TimeSpan.FromSeconds(5))), hub); Workspace = new(Codec, runs);
        }
        public async Task AssertNoIO() { Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Other.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Matrix.GetSnapshotAsync(CancellationToken.None)).Connected); }
    }
}

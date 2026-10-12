using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PlanSampleCatalogEditingTests
{
    [Theory]
    [InlineData("unknownMatrix")]
    [InlineData("unknownSource")]
    [InlineData("unknownMeter")]
    [InlineData("unguardedSource")]
    [InlineData("wrongMeterRole")]
    [InlineData("outsideInactiveDirection")]
    [InlineData("duplicateTerminal")]
    [InlineData("alreadyInitialized")]
    [InlineData("unreferencedConnection")]
    public async Task InitializationValidatesRuntimeCatalogAllDirectionsAndNeverOverwrites(string damage)
    {
        var b = new Bench(); var old = b.Plan; var prior = old.Routing!; var wire = prior.Wirings[0]; var sample = prior.Samples[0];
        var plan = damage == "alreadyInitialized" ? old : new ExperimentPlan(old.Name, new SequenceStep(new("root"), []), old.Preparations, old.Metadata, currentSourcePreparations: old.CurrentSourcePreparations, maximumStepExecutions: old.MaximumStepExecutions);
        var electrodes = sample.Electrodes.ToArray();
        if (damage == "outsideInactiveDirection") electrodes[^1] = new(electrodes[^1].Label, electrodes[^1].Channel, new("outside"));
        if (damage == "duplicateTerminal") electrodes[1] = new(electrodes[1].Label, electrodes[0].Channel, electrodes[0].Connection);
        sample = new(sample.Id, sample.Name, sample.Topology, electrodes, sample.Geometry);
        var wiring = damage switch
        {
            "unguardedSource" => prior.Wirings.Single(w => w.Id.Value == "unguarded"),
            "wrongMeterRole" => prior.Wirings.Single(w => w.Id.Value == "wrong-meter"),
            "unknownSource" => new(wire.Id, new("missing-source"), wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus),
            "unknownMeter" => new(wire.Id, wire.SourceMeter, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus, voltageMeter: new("missing-meter")),
            _ => wire,
        };
        var channels = prior.Channels.Select(channel => new ChannelProfile(channel.Id, channel.Name, channel.Connections.Where(connection => connection.Key.Value != "outside" || damage is "outsideInactiveDirection" or "unreferencedConnection"))).ToArray();
        var routing = new RoutingConfiguration(damage == "unknownMatrix" ? new("missing-matrix") : prior.Matrix, channels, [wiring], [sample]);
        var before = b.Codec.Serialize(plan); Assert.NotNull(Record.Exception(() => b.Workspace.InitializeRouting(plan, routing))); Assert.Equal(before, b.Codec.Serialize(plan)); await b.AssertNoIO();
    }
    [Fact]
    public async Task InitializationPreservesEnvelopeAndExistingStepsAndHasNoHardwareSideEffects()
    {
        var b = new Bench(); var old = b.Plan; var wire = old.Routing!.Wirings[0]; var routing = new RoutingConfiguration(old.Routing.Matrix, old.Routing.Channels.Select(channel => new ChannelProfile(channel.Id, channel.Name, channel.Connections.Where(connection => connection.Key.Value != "outside"))), [wire], old.Routing.Samples);
        var plan = new ExperimentPlan("new catalog", new SequenceStep(new("root"), [new DelayStep(new("wait"), TimeSpan.Zero)]), old.Preparations, old.Metadata, currentSourcePreparations: old.CurrentSourcePreparations, maximumStepExecutions: old.MaximumStepExecutions);
        var result = b.Workspace.InitializeRouting(plan, routing).Plan; Assert.Same(plan.Root, result.Root); Assert.Same(routing, result.Routing); Assert.Equal(plan.Name, result.Name); Assert.Equal(plan.Metadata, result.Metadata); Assert.Equal(plan.Preparations, result.Preparations); Assert.Equal(plan.MaximumStepExecutions, result.MaximumStepExecutions); Assert.Null(plan.Routing); await b.AssertNoIO();
    }
}

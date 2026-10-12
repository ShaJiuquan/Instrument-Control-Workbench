using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PlanSampleCatalogEditingTests
{
    [Theory]
    [InlineData("unguardedSource")]
    [InlineData("wrongMeterRole")]
    [InlineData("missingSource")]
    [InlineData("missingMeter")]
    [InlineData("outsidePort")]
    [InlineData("duplicateChannel")]
    [InlineData("duplicateWiring")]
    [InlineData("referencedChannel")]
    [InlineData("referencedConnection")]
    [InlineData("referencedWiring")]
    [InlineData("missingPhysicalAddress")]
    [InlineData("outsideUnusedConnection")]
    [InlineData("channelLimit")]
    [InlineData("wiringLimit")]
    public async Task CatalogEditingRejectsInvalidReferencesRolesAddressesAndLimitsBeforeIO(string damage)
    {
        var b = new Bench(); var prior = b.Plan.Routing!; var channels = prior.Channels.ToArray(); var wirings = prior.Wirings.ToArray(); var wire = wirings[0]; var channel = channels[0];
        switch (damage)
        {
            case "unguardedSource": wirings[0] = new(wire.Id, b.Other.Id, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus); break;
            case "wrongMeterRole": wirings[0] = new(wire.Id, b.Source.Id, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus, voltageMeter: b.Other.Id); break;
            case "missingSource": wirings[0] = new(wire.Id, new("missing"), wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus); break;
            case "missingMeter": wirings[0] = new(wire.Id, b.Source.Id, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus, voltageMeter: new("missing")); break;
            case "outsidePort": wirings[0] = new(wire.Id, b.Source.Id, new(1, 5), wire.SourceMinus, wire.SensePlus, wire.SenseMinus); break;
            case "duplicateChannel": channels = channels.Append(new(new(channel.Id.Value.ToUpperInvariant()), "Duplicate", channel.Connections)).ToArray(); break;
            case "duplicateWiring": wirings = wirings.Append(new(new(wire.Id.Value.ToUpperInvariant()), wire.SourceMeter, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus)).ToArray(); break;
            case "referencedChannel": channels = [new(new("other-channel"), "Other", channel.Connections)]; break;
            case "referencedConnection": channels[0] = new(channel.Id, channel.Name, channel.Connections.Skip(1)); break;
            case "referencedWiring": wirings = wirings.Skip(1).ToArray(); break;
            case "missingPhysicalAddress": channels = channels.Append(new(new("unbound"), "Unbound", [new(new("c"), null, null)])).ToArray(); break;
            case "outsideUnusedConnection": channels = channels.Append(new(new("outside"), "Outside", [new(new("c"), 1, 13)])).ToArray(); break;
            case "channelLimit": channels = channels.Concat(Enumerable.Range(0, 64).Select(i => new ChannelProfile(new("ch" + i), "Extra", [new(new("c"), 1, 1)]))).ToArray(); break;
            case "wiringLimit": wirings = wirings.Concat(Enumerable.Range(0, 64).Select(i => new InstrumentWiringProfile(new("wire" + i), wire.SourceMeter, wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus))).ToArray(); break;
        }
        var before = b.Codec.Serialize(b.Plan); Assert.NotNull(Record.Exception(() => b.Workspace.UpdateRoutingCatalog(b.Plan, channels, wirings))); Assert.Equal(before, b.Codec.Serialize(b.Plan)); await b.AssertNoIO();
    }
    [Fact]
    public async Task MetadataOnlyEditsRetainUnchangedImportedRowsAndRootEnvelope()
    {
        var b = new Bench(); var prior = b.Plan.Routing!; var channel = prior.Channels[0]; var old = channel.Connections[^1];
        var unbound = new ChannelConnection(new("unbound"), null, null, false, 2, "Unbound", 8, "legacy address", "Original");
        var extended = new ChannelProfile(channel.Id, channel.Name, channel.Connections.Append(unbound), "legacy-channel", 5, "alias", "old");
        var routing = new RoutingConfiguration(prior.Matrix, [extended], prior.Wirings, prior.Samples);
        var plan = new ExperimentPlan(b.Plan.Name, b.Plan.Root, b.Plan.Preparations, b.Plan.Metadata, routing, b.Plan.CurrentSourcePreparations, b.Plan.MaximumStepExecutions);
        var changed = new ChannelProfile(extended.Id, "Renamed", extended.Connections, extended.OriginalId, extended.GroupIndex, extended.Alias, "Updated");
        var result = b.Workspace.UpdateRoutingCatalog(plan, [changed], prior.Wirings).Plan; Assert.Same(plan.Root, result.Root); Assert.Equal(plan.Preparations, result.Preparations); Assert.Equal(plan.Metadata, result.Metadata); Assert.Equal(plan.MaximumStepExecutions, result.MaximumStepExecutions); Assert.Same(unbound, result.Routing!.Channels[0].Connections[^1]); Assert.Same(old, result.Routing.Channels[0].Connections[^2]); Assert.Equal(extended.GroupIndex, result.Routing.Channels[0].GroupIndex); Assert.Equal(extended.OriginalId, result.Routing.Channels[0].OriginalId);
        var roundtrip = b.Codec.Parse(b.Codec.Serialize(result)); Assert.Equal(unbound, roundtrip.Routing!.Channels[0].Connections[^1]); Assert.Equal("Renamed", roundtrip.Routing.Channels[0].Name); await b.AssertNoIO();
    }
    [Fact]
    public async Task RemovalCannotOrphanAnUnusedSampleAndUnusedProfilesCanBeRemoved()
    {
        var b = new Bench(); var prior = b.Plan.Routing!; var channel = prior.Channels[0];
        var unusedChannel = new ChannelProfile(new("unused"), "Unused", channel.Connections); var unusedSample = new SampleProfile(new("unused"), "Unused", prior.Samples[0].Topology, prior.Samples[0].Electrodes.Select(e => new SampleElectrode(e.Label, unusedChannel.Id, e.Connection)));
        var routing = new RoutingConfiguration(prior.Matrix, [channel, unusedChannel], prior.Wirings, prior.Samples.Append(unusedSample));
        var plan = new ExperimentPlan(b.Plan.Name, b.Plan.Root, b.Plan.Preparations, b.Plan.Metadata, routing, b.Plan.CurrentSourcePreparations, b.Plan.MaximumStepExecutions);
        Assert.Throws<ArgumentException>(() => b.Workspace.UpdateRoutingCatalog(plan, [channel], prior.Wirings));
        var result = b.Workspace.UpdateRoutingCatalog(plan, routing.Channels, [prior.Wirings[0]]).Plan; Assert.Single(result.Routing!.Wirings); Assert.Same(plan.Root, result.Root); Assert.Equal(2, result.Routing.Samples.Count); await b.AssertNoIO();
    }
}

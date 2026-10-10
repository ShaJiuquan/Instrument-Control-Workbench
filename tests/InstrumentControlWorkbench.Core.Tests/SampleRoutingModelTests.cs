using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SampleRoutingModelTests
{
    [Fact]
    public void TwoWireReusesPolarityPortsAndDeduplicatesCrosspoints()
    {
        var config = Config(SampleTopology.TwoWire, sharedPorts: true);
        var route = config.Resolve(new(new("SAMPLE"), new("WIRING"), SampleRouteSelection.TwoWireResistance));
        Assert.Equal(2, route.Closed.Count);
        Assert.Equal(new MatrixCrossPoint(1, 1, 1), route.Roles[0].Point); Assert.Equal(route.Roles[0].Point, route.Roles[2].Point);
        Assert.Equal(route.Roles[1].Point, route.Roles[3].Point);
        config.ValidateContext(route);
    }

    [Theory]
    [InlineData(SampleTopology.FourWire, SampleRouteSelection.FourWireResistance)]
    [InlineData(SampleTopology.HallBar, SampleRouteSelection.HallLongitudinal)]
    [InlineData(SampleTopology.HallBar, SampleRouteSelection.HallTransverse)]
    [InlineData(SampleTopology.VanDerPauw, SampleRouteSelection.VdpAbCd)]
    public void FourTerminalModesRequireDistinctPorts(SampleTopology topology, SampleRouteSelection selection)
        => Assert.Throws<ArgumentException>(() => Config(topology, sharedPorts: true).Resolve(new(new("sample"), new("wiring"), selection)));

    [Theory]
    [InlineData(SampleRouteSelection.VdpAbCd, "A", "B", "C", "D")]
    [InlineData(SampleRouteSelection.VdpBaCd, "B", "A", "C", "D")]
    [InlineData(SampleRouteSelection.VdpAbDc, "A", "B", "D", "C")]
    [InlineData(SampleRouteSelection.VdpBaDc, "B", "A", "D", "C")]
    [InlineData(SampleRouteSelection.VdpBcDa, "B", "C", "D", "A")]
    [InlineData(SampleRouteSelection.VdpCbDa, "C", "B", "D", "A")]
    [InlineData(SampleRouteSelection.VdpBcAd, "B", "C", "A", "D")]
    [InlineData(SampleRouteSelection.VdpCbAd, "C", "B", "A", "D")]
    public void VdpResolvesTheEightPolarityAndOrientationAssignments(SampleRouteSelection selection, string a, string b, string c, string d)
    {
        var route = Config(SampleTopology.VanDerPauw).Resolve(new(new("sample"), new("wiring"), selection));
        Assert.Equal(new[] { a, b, c, d }, route.Roles.Select(r => r.Electrode.Value));
        Assert.Equal(4, route.Closed.Count);
    }

    [Theory]
    [InlineData("disabled_electrode")]
    [InlineData("disabled_connection")]
    [InlineData("missing_column")]
    [InlineData("missing_channel")]
    [InlineData("missing_connection")]
    [InlineData("slot_mismatch")]
    public void BrokenLinksAreExplicitFailures(string failure)
    {
        var config = Config(SampleTopology.FourWire);
        var channels = config.Channels.ToArray(); var electrodes = config.Samples[0].Electrodes.ToArray();
        var first = channels[0].Connections[0];
        switch (failure)
        {
            case "disabled_electrode": electrodes[0] = new(electrodes[0].Label, electrodes[0].Channel, electrodes[0].Connection, false); break;
            case "missing_channel": electrodes[0] = new(electrodes[0].Label, new("missing"), electrodes[0].Connection); break;
            case "missing_connection": electrodes[0] = new(electrodes[0].Label, electrodes[0].Channel, new("missing")); break;
            default:
                var items = channels[0].Connections.ToArray();
                items[0] = new(first.Key, failure == "slot_mismatch" ? 2 : 1, failure == "missing_column" ? null : first.Column, failure != "disabled_connection");
                channels[0] = new(channels[0].Id, channels[0].Name, items); break;
        }
        config = new(config.Matrix, channels, config.Wirings, [new(config.Samples[0].Id, "sample", SampleTopology.FourWire, electrodes)]);
        Assert.NotNull(Record.Exception(() => config.Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance))));
    }

    [Fact]
    public void CrossChannelLinksResolveAndDeclaredSlotMustMatchTheInstrumentPort()
    {
        var config = Config(SampleTopology.FourWire);
        var channels = new[] { config.Channels[0], new ChannelProfile(new("other"), "other", [new(new("v"), null, 6)]) };
        var electrodes = config.Samples[0].Electrodes.ToArray(); electrodes[2] = new(electrodes[2].Label, new("OTHER"), new("V"));
        config = new(config.Matrix, channels, config.Wirings, [new(new("sample"), "sample", SampleTopology.FourWire, electrodes)]);
        var route = config.Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance));
        Assert.Equal(new MatrixCrossPoint(1, 3, 6), route.Roles[2].Point); Assert.Equal("other", route.Roles[2].Channel.Value);
    }

    [Fact]
    public void DuplicateSampleTerminalsAndOppositePolarityPortCollisionsAreRejected()
    {
        var config = Config(SampleTopology.FourWire); var connections = config.Channels[0].Connections.ToArray();
        connections[1] = new(connections[1].Key, 1, 1);
        var channels = new[] { new ChannelProfile(new("channel"), "channel", connections) };
        var invalid = new RoutingConfiguration(config.Matrix, channels, config.Wirings, config.Samples);
        Assert.Throws<ArgumentException>(() => invalid.Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance)));
        config = Config(SampleTopology.TwoWire);
        var wrongPorts = new InstrumentWiringProfile(new("wiring"), new("source"), new(1, 1), new(1, 1), new(1, 3), new(1, 4));
        invalid = new(config.Matrix, config.Channels, [wrongPorts], config.Samples);
        Assert.Throws<ArgumentException>(() => invalid.Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.TwoWireResistance)));
    }

    [Fact]
    public void ConfigurationAndContextsDefensivelyCopyInputsAndRejectChangedProvenance()
    {
        var config = Config(SampleTopology.FourWire); var samples = config.Samples.ToArray();
        var copy = new RoutingConfiguration(config.Matrix, config.Channels, config.Wirings, samples); samples[0] = Config(SampleTopology.TwoWire).Samples[0];
        var route = copy.Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance));
        var roles = route.Roles.ToArray(); var snapshot = new ResolvedSampleRoute(route.Binding, route.SampleName, route.Topology, route.Matrix, route.SourceMeter, roles);
        roles[0] = new(roles[0].Role, roles[0].Electrode, roles[0].Channel, roles[0].Connection, new(1, 1, 5));
        Assert.Equal(route.Roles[0], snapshot.Roles[0]);
        var changed = new ResolvedSampleRoute(route.Binding, route.SampleName, route.Topology, route.Matrix, route.SourceMeter, roles);
        Assert.Throws<ArgumentException>(() => copy.ValidateContext(changed));
        Assert.Throws<ArgumentException>(() => new ChannelProfile(new("channel"), "channel", [new(new("A"), 1, 1), new(new("a"), 1, 2)]));
        Assert.Throws<ArgumentException>(() => new SampleProfile(new("sample"), "sample", SampleTopology.FourWire, [new(new("I+"), new("channel"), new("a"))]));
    }

    [Fact]
    public void MeasurementSelectionMustMatchSampleTopology()
        => Assert.Throws<ArgumentException>(() => Config(SampleTopology.TwoWire).Resolve(new(new("sample"), new("wiring"), SampleRouteSelection.HallLongitudinal)));

    private static RoutingConfiguration Config(SampleTopology topology, bool sharedPorts = false)
    {
        var labels = SampleProfile.RequiredLabels(topology);
        var connections = labels.Select((label, i) => new ChannelConnection(new($"p{i}"), 1, i + 1)).ToArray();
        var electrodes = labels.Select((label, i) => new SampleElectrode(new(label), new("channel"), new($"p{i}"))).ToArray();
        return new(new("matrix"), [new(new("channel"), "channel", connections)],
            [new(new("wiring"), new("source"), new(1, 1), new(1, 2), new(1, sharedPorts ? 1 : 3), new(1, sharedPorts ? 2 : 4))],
            [new(new("sample"), "sample", topology, electrodes)]);
    }
}

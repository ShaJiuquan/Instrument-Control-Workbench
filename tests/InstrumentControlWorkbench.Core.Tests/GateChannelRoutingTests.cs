using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class GateChannelRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GateRoutingUsesOnlySourcePortsAndPrefersBiasVocabulary(bool legacy)
    {
        var profile = Profile(legacy: legacy); var route = profile.ResolveGate("contacts");
        Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 5), new(1, 2, 6) }, route.Route.Closed); Assert.Equal(TimeSpan.FromMilliseconds(20), route.Route.Settle); Assert.Null(route.VoltageMeter);
        Assert.Same(profile.Catalog.Channels[0], profile.Catalog.ResolveChannel("__active__"));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("disabled")]
    [InlineData("slot")]
    [InlineData("terminal")]
    [InlineData("port")]
    [InlineData("meter")]
    public void GateRoutingRejectsIncompleteUnsafeOrIndependentCircuits(string damage) => Assert.Throws<ArgumentException>(() => Profile(damage).ResolveGate("contacts"));

    private static ImportChannelRouting Profile(string? damage = null, bool legacy = false)
    {
        var connections = new List<ChannelConnection>();
        if (damage != "missing")
        {
            connections.Add(new(new(legacy ? "gate_plus" : "bias_plus"), damage == "slot" ? 2 : 1, 5));
            if (damage != "partial") connections.Add(new(new(legacy ? "gate_minus" : "bias_minus"), 1, damage == "terminal" ? 5 : 6, damage != "disabled"));
        }
        connections.Add(new(new("i_plus"), 1, 1)); connections.Add(new(new("i_minus"), 1, 2));
        if (!legacy && damage is null) { connections.Add(new(new("gate_plus"), 1, 9)); connections.Add(new(new("gate_minus"), 1, 10)); }
        var channel = new ChannelProfile(new("channel1"), "CH1", connections, alias: "contacts");
        var layout = new MatrixWiringLayout(new("wire"), "Ports", new(1, 1), new(1, damage == "port" ? 1 : 2), new(1, 3), new(1, 4));
        var sample = new SampleProfile(new("sample"), "Sample", SampleTopology.TwoWire, [new(new("A"), channel.Id, new("i_plus")), new(new("B"), channel.Id, new("i_minus"))]);
        var catalog = new RoutingProfileCatalog(channel.Id, sample.Id, [channel], [sample], [layout], []);
        var wire = new InstrumentWiringProfile(layout.Id, new("source"), layout.SourcePlus, layout.SourceMinus, layout.SensePlus, layout.SenseMinus, voltageMeter: damage == "meter" ? new InstrumentId("meter") : null);
        return new(catalog, new(new("matrix"), [channel], [wire], [sample]), layout.Id, TimeSpan.FromMilliseconds(20));
    }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ElectrodeContinuityModelTests
{
    [Theory]
    [InlineData(ElectrodePair.Source, 1, 2)]
    [InlineData(ElectrodePair.Meter, 3, 4)]
    [InlineData(ElectrodePair.Bias, 5, 6)]
    [InlineData(ElectrodePair.Gate, 7, 8)]
    public void EveryPairUsesForceRowsRatherThanDisplayOrSenseRows(ElectrodePair pair, int plus, int minus)
    {
        var profile = Profile(); var route = profile.ResolveContinuity(new("channel"), pair)!; Assert.Equal(new[] { new MatrixCrossPoint(1, 1, plus), new(1, 2, minus) }, route.Closed);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("slot")]
    public void UnconfiguredPairsNeverBecomeExecutableContacts(string damage) => Assert.Null(Profile(damage).ResolveContinuity(new("channel"), ElectrodePair.Meter));
    [Fact]
    public void EarlierDisabledAliasDoesNotHideEnabledLegacyAlias() => Assert.Equal(3, Profile("alias").ResolveContinuity(new("channel"), ElectrodePair.Meter)!.Closed[0].Column);
    [Theory]
    [InlineData("voltage")]
    [InlineData("mode")]
    [InlineData("threshold")]
    [InlineData("unit")]
    [InlineData("duration")]
    [InlineData("columns")]
    [InlineData("ports")]
    [InlineData("terminals")]
    public void InvalidDiagnosticsAreRejectedAtTypedBoundary(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ReadElectrodeContinuityStep(new("probe"), new(new("source"), SourceFunction.Voltage, new(damage == "voltage" ? 211 : .1, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, damage == "mode" ? SensingMode.FourWire : SensingMode.TwoWire, 1, true)), new("channel"), ElectrodePair.Source, damage is "ports" or "terminals" ? new MatrixRouteExpectation(new("matrix"), [new(1, 1, 1), new(1, damage == "ports" ? 1 : 2, damage == "terminals" ? 1 : 2)]) : null, damage == "duration" ? TimeSpan.FromSeconds(-1) : TimeSpan.Zero, new(damage == "threshold" ? 0 : 1e6, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Ohm), new("status"), new(damage == "columns" ? "status" : "reason"), new("v"), new("i"), new("r")));
    }
    private static ImportChannelRouting Profile(string? damage = null)
    {
        var keys = new[] { "i_plus", "i_minus", "v_plus", "v_minus", "bias_plus", "bias_minus", "gate", "ground" };
        var connections = keys.Select((key, i) => new ChannelConnection(new(key), damage == "slot" && i == 2 ? 2 : 1, i + 1, damage != "disabled" || i != 2, displayRow: 8)).ToList();
        if (damage == "missing") connections.RemoveAt(2);
        if (damage == "alias") { connections[2] = new(new("v_plus"), 1, 9, false); connections.Insert(3, new(new("meter_plus"), 1, 3)); }
        var channel = new ChannelProfile(new("channel"), "Channel", connections); var sample = new SampleProfile(new("sample"), "Sample", SampleTopology.TwoWire, [new(new("A"), channel.Id, new("i_plus")), new(new("B"), channel.Id, new("i_minus"))]);
        var layout = new MatrixWiringLayout(new("wire"), "Force", new(1, 1), new(1, 2), new(1, 3), new(1, 4)); var catalog = new RoutingProfileCatalog(channel.Id, sample.Id, [channel], [sample], [layout], []);
        return new(catalog, new(new("matrix"), [channel], [new(layout.Id, new("source"), layout.SourcePlus, layout.SourceMinus, layout.SensePlus, layout.SenseMinus)], [sample]), layout.Id, TimeSpan.Zero);
    }
}

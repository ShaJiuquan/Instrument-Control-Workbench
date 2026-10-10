using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ImportRoutingBindingTests
{
    [Theory]
    [InlineData("SAMPLE1")]
    [InlineData(" 1 ")]
    [InlineData("shared name")]
    [InlineData("old-key")]
    public void SampleQueriesReturnCanonicalProfile(string query)
    {
        var config = Configuration(); var aliases = new Dictionary<string, SampleId> { ["old-key"] = new("sample1") };
        var modes = new Dictionary<string, WiringProfileId> { ["volt_dc"] = new("wiring") };
        var binding = new ImportSampleRouting(config, modes, aliases, TimeSpan.FromMilliseconds(10));
        modes.Clear(); aliases.Clear(); Assert.Equal("sample1", binding.ResolveSample(query).Id.Value);
        Assert.Equal(TimeSpan.FromMilliseconds(10), binding.RouteSettle); Assert.Single(binding.ModeWirings); Assert.Single(binding.Aliases);
    }
    [Theory]
    [InlineData("missing-wiring")]
    [InlineData("missing-sample")]
    [InlineData("duplicate-alias")]
    [InlineData("shadow")]
    [InlineData("duration")]
    public void InvalidImportBindingGraphsReject(string fault)
    {
        var config = Configuration(); var modes = new[] { KeyValuePair.Create("volt_dc", new WiringProfileId(fault == "missing-wiring" ? "unknown" : "wiring")) };
        var aliases = fault switch
        {
            "missing-sample" => new[] { KeyValuePair.Create("old", new SampleId("unknown")) },
            "shadow" => new[] { KeyValuePair.Create("sample1", new SampleId("sample2")) },
            "duplicate-alias" => new[] { KeyValuePair.Create("old", new SampleId("sample1")), KeyValuePair.Create("OLD", new SampleId("sample1")) },
            _ => [],
        };
        Assert.ThrowsAny<ArgumentException>(() => new ImportSampleRouting(config, modes, aliases, fault == "duration" ? TimeSpan.FromSeconds(-1) : TimeSpan.Zero));
    }
    [Fact]
    public void AmbiguousDisplayNamesNeverSelectAnArbitrarySample()
    {
        var binding = new ImportSampleRouting(Configuration(duplicateName: true), [KeyValuePair.Create("volt_dc", new WiringProfileId("wiring"))]);
        Assert.Throws<ArgumentException>(() => binding.ResolveSample("shared name")); Assert.Equal("sample2", binding.ResolveSample("sample2").Id.Value);
        Assert.Throws<ArgumentException>(() => binding.ResolveSample("missing"));
    }
    [Fact]
    public void MatrixVerificationFreezesInputAndRejectsDuplicateOrOversizedPoints()
    {
        var points = new List<MatrixCrossPoint> { new(1, 1, 1) }; var step = new VerifyMatrixRouteStep(new("verify"), new("matrix"), points);
        points.Clear(); Assert.Single(step.Closed);
        Assert.Throws<ArgumentException>(() => new VerifyMatrixRouteStep(new("v"), new("matrix"), [new(1, 1, 1), new(1, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new VerifyMatrixRouteStep(new("v"), new("matrix"), Enumerable.Range(1, 4097).Select(i => new MatrixCrossPoint(1, 1, i))));
    }
    [Fact]
    public void AcquisitionSettleCannotBeNegative()
    {
        var read = new ReadResistanceStep(new("r"), new("source"), Instruments.SourceFunction.Voltage, new("v"), new("i"), new("r"), new(0, Parameters.PhysicalUnit.Ampere));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadSampleResistanceStep(read, new(new("sample"), new("wiring"), SampleRouteSelection.TwoWireResistance), TimeSpan.Zero, acquisitionSettle: TimeSpan.FromSeconds(-1)));
    }
    private static RoutingConfiguration Configuration(bool duplicateName = false)
    {
        var channel = new ChannelProfile(new("channel"), "Channel", [new(new("a"), 1, 1), new(new("b"), 1, 2)]);
        var electrodes = new[] { new SampleElectrode(new("A"), channel.Id, new("a")), new(new("B"), channel.Id, new("b")) };
        return new(new("matrix"), [channel], [new(new("wiring"), new("source"), new(1, 1), new(1, 2), new(1, 1), new(1, 2))],
            [new(new("sample1"), "shared name", SampleTopology.TwoWire, electrodes), new(new("sample2"), duplicateName ? "shared name" : "other name", SampleTopology.TwoWire, electrodes)]);
    }
}

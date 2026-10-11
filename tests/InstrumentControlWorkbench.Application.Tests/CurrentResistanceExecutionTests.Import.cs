using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class CurrentResistanceExecutionTests
{
    [Fact]
    public async Task PublishedIndependentFixturesExecuteNativeAndPpmsPlansThroughConfiguredRuntime()
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "current-resistance-devices.json"), Token);
        var native = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "current-resistance.plan.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
        var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(native, Token);
        Assert.Equal(100, Assert.Single(Assert.Single(runs.LatestDataSet!.Rows).IndependentResistance).Resistance.Value, 8);
        var imported = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-current-resistance-import.json"), runtime.CreateImportBindings(native.Preparations), Token);
        await runs.StartPlanAsync(imported, Token); Assert.Equal(2, runs.LatestDataSet.Rows.Count);
        Assert.All(runs.LatestDataSet.Rows, row => Assert.Equal(row.Values[new("ppms.root.axis")], Assert.Single(row.IndependentResistance).Current));
        Assert.False(runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PpmsOwnedCurrentResistanceUsesExplicitRolesRangesAndMeterInput(bool routed)
    {
        await using var b = new Bench();
        await b.Meter.ConnectAsync(Token); await b.Meter.ConfigureAsync(new(1, null, 3, true, 17, AveragingMode.Moving, 2), Token);
        var p = new JsonObject
        {
            ["channel"] = routed ? "ch1" : "none",
            ["excitation_a"] = "1e-5",
            ["compliance"] = 10,
            ["current_range_a"] = "0.00005",
            ["voltage_range_v"] = "0.2",
            ["meter_nplc"] = 2,
            ["k2182_channel"] = 2,
            ["measure_settle_s"] = 0
        };
        var plan = new PpmsExperimentPlanImporter().Parse(CurrentJson(p), CurrentBindings(b, routed));
        Assert.Empty(b.Peer.Server.Commands); await b.Runs.StartPlanAsync(plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var evidence = Assert.Single(row.IndependentResistance);
        Assert.Equal(200, evidence.Resistance.Value, 8); Assert.Equal(CurrentReference.ProgrammedSetpoint, evidence.CurrentReference);
        Assert.Equal(new(0.0001, PhysicalUnit.Ampere), evidence.SourceConfiguration.ManualRange);
        var meter = row.Quality.Single(q => q.MeterConfiguration is not null).MeterConfiguration!;
        Assert.Equal(2, meter.Channel); Assert.Equal(new(1, PhysicalUnit.Volt), meter.ManualRange); Assert.Equal(2, meter.Nplc);
        Assert.True(meter.FilterEnabled); Assert.Equal(17, meter.FilterCount); Assert.Equal(AveragingMode.Moving, meter.FilterMode); Assert.Equal(2, meter.FilterWindowPercent);
        Assert.Equal(routed ? b.Route : null, evidence.Route); Assert.False(b.Peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentScanOverridesChildCurrentAndPreservesItsSourceOwnership(bool routed)
    {
        await using var b = new Bench(); var node = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject { ["points"] = new JsonArray(-1e-5, 2e-5), ["channel"] = routed ? "ch1" : "none", ["settle_s"] = 0, ["compliance_v"] = 10 },
            ["children"] = new JsonArray(JsonNode.Parse(CurrentJson(new JsonObject { ["current_a"] = "unused child value", ["source_managed_externally"] = false, ["settle_s"] = 20, ["compliance_v"] = 7 })))
        };
        var plan = new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), CurrentBindings(b, routed));
        Assert.Contains(plan.Metadata.Keys, k => k.EndsWith("curr_dc_managed_settle_s_unused", StringComparison.Ordinal));
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(2, rows.Count); Assert.Equal(new[] { -100d, 50d }, rows.Select(r => Assert.Single(r.IndependentResistance).Resistance.Value));
        Assert.Equal(new[] { -1e-5, 2e-5 }, rows.Select(r => r.Values[new("ppms.root.axis")].Value));
        Assert.All(rows, row => { var e = Assert.Single(row.IndependentResistance); Assert.Equal(row.Values[new("ppms.root.axis")], e.Current); Assert.Equal(10, e.SourceConfiguration.Compliance); });
        Assert.Equal(1, b.Peer.Server.Commands.Count(c => c == ":OUTP ON")); Assert.False(b.Peer.Output);
    }
    [Fact]
    public async Task ExplicitManagedCurrentMustMatchActualSourceBeforeReadingMeter()
    {
        await using var b = new Bench(); var imported = new PpmsExperimentPlanImporter().Parse(CurrentJson(new JsonObject { ["channel"] = "none", ["current_a"] = 2e-5, ["source_managed_externally"] = true }), CurrentBindings(b));
        var plan = b.Plan(imported.Root); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.Equal(0, b.Meter.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Peer.Output);
    }
    [Theory]
    [InlineData("current_a", "0")]
    [InlineData("current_a", "1e-18")]
    [InlineData("sense_channel", "1.5")]
    [InlineData("nplc", "51")]
    [InlineData("meter_range_v", "121")]
    [InlineData("source_range_a", "4")]
    [InlineData("source_instrument", "\"k2182\"")]
    [InlineData("meter_instrument", "\"k6221\"")]
    [InlineData("sense_mode", "\"kelvin\"")]
    [InlineData("current", "0.001")]
    public async Task InvalidCurrentImportExplainsNodeAndLineBeforeAnySourceConnection(string field, string json)
    {
        await using var b = new Bench(); var p = new JsonObject { ["channel"] = "none", ["current_a"] = 1e-5 }; p[field] = JsonNode.Parse(json);
        var node = JsonNode.Parse(CurrentJson(p))!.AsObject(); node["source_line"] = 19;
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), CurrentBindings(b)));
        Assert.Contains("ppms.root", error.Message); Assert.Contains("source line 19", error.Message); Assert.Empty(b.Peer.Server.Commands);
    }
    [Fact]
    public async Task CurrentRoutingCannotReuseAnAmbiguousSourceOnlyBinding()
    {
        await using var b = new Bench(); var channels = new Dictionary<string, ImportChannelRoute> { ["ch1"] = new(b.Source.Id, new(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero)) };
        var bindings = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k6221"] = b.Source.Id, ["k2182"] = b.Meter.Descriptor.Id }, [], [b.Preparation], channels, standaloneVoltageMeters: [b.Meter.Descriptor.Id]);
        Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(CurrentJson(new()), bindings)); Assert.Empty(b.Peer.Server.Commands);
    }
    private static string CurrentJson(JsonObject p) => new JsonObject { ["type"] = "measurement", ["name"] = "meas_r", ["params"] = p }.ToJsonString();
    private static ExperimentImportBindings CurrentBindings(Bench b, bool routed = false) => new(
        new Dictionary<string, InstrumentId> { ["k6221"] = b.Source.Id, ["k2182"] = b.Meter.Descriptor.Id }, [], [b.Preparation],
        routed ? new Dictionary<string, ImportChannelRoute> { ["ch1"] = new(b.Source.Id, new(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero), b.Meter.Descriptor.Id) } : [],
        standaloneVoltageMeters: [b.Meter.Descriptor.Id]);
}

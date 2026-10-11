using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Fact]
    public async Task PublishedPureSourceFixturesRunNativeAndImportedScansWithoutSmuPreparation()
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "k6221-meter-devices.json"), Token);
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "k6221-current-resistance.plan.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub); var runs = new MeasurementRunService(runtime.Registry, hub);
        Assert.Empty(runtime.Sources); Assert.Empty(plan.Preparations); await runs.StartPlanAsync(plan, Token);
        Assert.Equal(CurrentSourceModel.Keithley6221, Assert.Single(Assert.Single(runs.LatestDataSet!.Rows).IndependentResistance).SourceConfiguration.Model);
        var imported = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-current-resistance-import.json"), runtime.CreateImportBindings(currentSourcePreparations: plan.CurrentSourcePreparations), Token);
        await runs.StartPlanAsync(imported, Token); Assert.Equal(2, runs.LatestDataSet.Rows.Count); Assert.Empty(imported.Preparations);
    }
    [Theory]
    [InlineData("current")]
    [InlineData("excitation_a")]
    [InlineData("i_a")]
    public async Task OwnedPpmsResistanceUsesK6221LimitsAndNeverIssuesSmuSensingWrites(string alias)
    {
        await using var b = new Bench(); var p = new JsonObject { ["channel"] = "ch1", [alias] = "-1e-5", ["compliance"] = 105, ["current_range_a"] = "12e-6", ["k2182_channel"] = 2, ["settle_s"] = 0 };
        var plan = new PpmsExperimentPlanImporter().Parse(ResistanceJson(p), Bindings(b, true));
        Assert.Empty(plan.Preparations); Assert.Single(plan.CurrentSourcePreparations); Assert.DoesNotContain("k2450.", new JsonExperimentPlanDocument().Serialize(plan));
        await b.Runs.StartPlanAsync(plan, Token); var e = Assert.Single(Assert.Single(b.Runs.LatestDataSet!.Rows).IndependentResistance);
        Assert.Equal(-200, e.Resistance.Value, 8); Assert.Equal(105, e.SourceConfiguration.Compliance); Assert.Equal(new(20e-6, PhysicalUnit.Ampere), e.SourceConfiguration.ManualRange);
        Assert.Equal(CurrentSourceModel.Keithley6221, e.SourceConfiguration.Model); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedPureCurrentScanInjectsActualSignedAxisAndKeepsParentConfiguration(bool routed)
    {
        await using var b = new Bench(); var scan = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5, -2e-5), ["settle_s"] = 0, ["channel"] = routed ? "ch1" : "none", ["source_range_a"] = 20e-6, ["compliance_v"] = 105 },
            ["children"] = new JsonArray(JsonNode.Parse(ResistanceJson(new JsonObject { ["current_a"] = "unused", ["compliance_v"] = 10, ["settle_s"] = 30 })))
        };
        var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), Bindings(b, routed)); await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(1, b.Source.Enables);
        Assert.All(b.Runs.LatestDataSet.Rows, row => { var e = Assert.Single(row.IndependentResistance); Assert.Equal(row.Values[new("ppms.root.axis")], e.Current); Assert.Equal(105, e.SourceConfiguration.Compliance); });
        Assert.Equal(100, b.Runs.LatestDataSet.Rows[0].Values.Single(v => v.Key.Value.EndsWith(".R_ohms", StringComparison.Ordinal)).Value.Value, 8);
        Assert.Equal(-50, b.Runs.LatestDataSet.Rows[1].Values.Single(v => v.Key.Value.EndsWith(".R_ohms", StringComparison.Ordinal)).Value.Value, 8);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task GenericCurrentCommandAndConcreteK6221AliasesUseConfirmedParameters()
    {
        await using var b = new Bench(); var set = new JsonObject { ["type"] = "command", ["name"] = "set_current", ["params"] = new JsonObject { ["target_a"] = -1e-5, ["source_range_a"] = 12e-6, ["compliance_v"] = 105, ["channel"] = "none" } };
        var plan = new PpmsExperimentPlanImporter().Parse(set.ToJsonString(), Bindings(b)); await b.Runs.StartPlanAsync(plan, Token);
        var state = (await b.Source.GetSnapshotAsync(Token)).Settings!; Assert.Equal(-1e-5, state.Current.Value); Assert.Equal(105, state.Compliance.Value); Assert.Equal(new(20e-6, PhysicalUnit.Ampere), state.ManualRange);
        var nodes = new JsonArray();
        foreach (var (name, p) in new (string, JsonObject)[] {
            ("k6221.set_current", new() { ["current"] = 0 }), ("k6221.set_compliance", new() { ["compliance_v"] = 100 }),
            ("k6221.set_source_range", new() { ["range"] = -12e-6 }), ("k6221.set_current", new() { ["current_a"] = 1e-5 }),
            ("k6221.set_output", new() { ["output_enabled"] = true }), ("k6221.set_source_auto_range", new() { ["auto_range"] = true }), ("k6221.set_output", new() { ["enabled"] = false }) })
            nodes.Add(new JsonObject { ["type"] = "command", ["name"] = name, ["params"] = p });
        var sequence = new JsonObject { ["type"] = "sequence", ["children"] = nodes };
        await b.Runs.StartPlanAsync(new PpmsExperimentPlanImporter().Parse(sequence.ToJsonString(), Bindings(b)), Token);
        state = (await b.Source.GetSnapshotAsync(Token)).Settings!; Assert.Equal(1e-5, state.Current.Value); Assert.Equal(100, state.Compliance.Value); Assert.Null(state.ManualRange); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task OffOnlyK6221CommandDoesNotApplyAnUnusedPreparation()
    {
        await using var b = new Bench(); await b.Registry.ConnectAsync(b.Source.Descriptor.Id, Token);
        var actual = new Keithley6221Settings(new(2e-5, PhysicalUnit.Ampere), new(30, PhysicalUnit.Volt)); await b.Source.ConfigureAsync(actual, Token); await b.Source.SetOutputAsync(true, Token);
        var node = new JsonObject { ["type"] = "command", ["name"] = "k6221.set_output", ["params"] = new JsonObject { ["enabled"] = false } };
        var plan = new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), Bindings(b)); Assert.Empty(plan.CurrentSourcePreparations);
        await b.Runs.StartPlanAsync(plan, Token); var state = await b.Source.GetSnapshotAsync(Token); Assert.Equal(actual, state.Settings); Assert.False(state.OutputEnabled);
    }
    [Theory]
    [InlineData("current_a", "0.106")]
    [InlineData("compliance_v", "0.09")]
    [InlineData("source_range_a", "0.106")]
    [InlineData("source_instrument", "\"missing\"")]
    [InlineData("meter_instrument", "\"k6221\"")]
    public async Task InvalidPpmsPureSourceFailsBeforeAnyConnectionWithNodeDiagnostic(string key, string value)
    {
        await using var b = new Bench(); var p = new JsonObject { ["channel"] = "none" }; p[key] = JsonNode.Parse(value);
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(ResistanceJson(p), Bindings(b)));
        Assert.Contains("ppms.root", error.Message); Assert.Equal(0, b.Source.Connections);
    }
    [Fact]
    public async Task PureConfiguredRuntimeNeedsNoPrimarySmuAndRoundtripsSourceCategory()
    {
        var catalog = new DeviceConfigurationCatalog(null, [new(new("source"), "K6221", "Explicit pure source", ConfiguredDeviceCategory.Source, new SimulationDeviceConnection()), new(new("meter"), "K2182", "External simulated voltage", ConfiguredDeviceCategory.Meter, new SimulationDeviceConnection())],
            new Dictionary<string, InstrumentId> { ["k6221"] = new("source"), ["k2182"] = new("meter") });
        var doc = new JsonDeviceConfigurationDocument(); var loaded = doc.Parse(doc.Serialize(catalog)); var hub = new NotificationHub();
        var runtime = new ConfiguredDeviceRuntime(loaded, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub); Assert.Empty(runtime.Sources); Assert.Throws<InvalidOperationException>(() => runtime.PrimarySource);
        var prep = new CurrentSourcePreparation(new("source"), new(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)));
        var plan = new PpmsExperimentPlanImporter().Parse(ResistanceJson(new() { ["channel"] = "none", ["settle_s"] = 0 }), runtime.CreateImportBindings(currentSourcePreparations: [prep]));
        var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(plan, Token); Assert.Equal(CurrentSourceModel.Keithley6221, Assert.Single(Assert.Single(runs.LatestDataSet!.Rows).IndependentResistance).SourceConfiguration.Model);
    }
    [Theory]
    [InlineData("tcp")]
    [InlineData("visa")]
    [InlineData("serial")]
    public void PhysicalK6221RejectsTheWholeCatalogBeforeConstructingAnyDevice(string transport)
    {
        DeviceConnection connection = transport switch { "tcp" => new TcpDeviceConnection("127.0.0.1", 12345, "\n", TimeSpan.FromSeconds(2)), "visa" => new VisaDeviceConnection("GPIB0::12::INSTR"), _ => new SerialDeviceConnection("test-port", TimeSpan.FromSeconds(2)) };
        var catalog = new DeviceConfigurationCatalog(null, [new(new("sim"), "K6221", "Simulator", ConfiguredDeviceCategory.Source, new SimulationDeviceConnection()), new(new("physical"), "K6221", "Unsupported real adapter", ConfiguredDeviceCategory.Source, connection)], []);
        var factory = new CountingFactory(); Assert.Throws<NotSupportedException>(() => new ConfiguredDeviceRuntime(catalog, factory, new(TimeSpan.FromSeconds(5)), new())); Assert.Equal(0, factory.Created);
    }
    private static string ResistanceJson(JsonObject p) => new JsonObject { ["type"] = "measurement", ["name"] = "meas_r", ["params"] = p }.ToJsonString();
    private static ExperimentImportBindings Bindings(Bench b, bool routed = false) => new(new Dictionary<string, InstrumentId> { ["k6221"] = b.Source.Descriptor.Id, ["k2182"] = b.Meter.Descriptor.Id }, [],
        channels: routed ? new Dictionary<string, ImportChannelRoute> { ["ch1"] = new(b.Source.Descriptor.Id, new(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero), b.Meter.Descriptor.Id) } : [],
        standaloneVoltageMeters: [b.Meter.Descriptor.Id], currentSourcePreparations: [b.Preparation]);
    private sealed class CountingFactory : IConfiguredDeviceFactory
    {
        private readonly ConfiguredDeviceFactory inner = new(); public int Created { get; private set; }
        public void Validate(DeviceConfiguration device) => inner.Validate(device);
        public ConfiguredDevicePorts Create(DeviceConfiguration device) { Created++; return inner.Create(device); }
    }
}

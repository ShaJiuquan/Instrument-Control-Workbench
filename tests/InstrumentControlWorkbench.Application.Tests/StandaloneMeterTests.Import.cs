using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class StandaloneMeterTests
{
    [Fact]
    public async Task PublishedMeterOnlyFixturesRunBothInputsWithPerRowConfiguration()
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "standalone-meter-devices.json"), Token);
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "standalone-meter.plan.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
        var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(plan, Token);
        Assert.Empty(runtime.Sources); Assert.Equal(new[] { 0.001, 0.002 }, runs.LatestDataSet!.Rows.Select(r => r.Values[new("voltage")].Value));
        Assert.Equal(new[] { 1, 2 }, runs.LatestDataSet.Rows.Select(r => Assert.Single(r.Quality).MeterConfiguration!.Channel));
        Assert.All(runs.LatestDataSet.Rows, r => Assert.Equal(AveragingMode.Moving, Assert.Single(r.Quality).MeterConfiguration!.FilterMode));
    }
    [Fact]
    public async Task MeterOnlyConfigurationExecutesWithoutSourcePreparationOrFallback()
    {
        var c = new DeviceConfigurationCatalog(null, [new(new("nanovoltmeter"), "K2182", "Explicit simulated meter", ConfiguredDeviceCategory.Meter, new SimulationDeviceConnection())],
            new Dictionary<string, InstrumentId> { ["k2182"] = new("nanovoltmeter") });
        var doc = new JsonDeviceConfigurationDocument(); var loaded = doc.Parse(doc.Serialize(c));
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(loaded, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
        Assert.Empty(runtime.Sources); var descriptor = Assert.Single(runtime.Registry.Devices); Assert.Equal(DeviceStopPolicy.AbortAcquisition, descriptor.StopPolicy);
        Assert.Empty((await runtime.Registry.GetStatusAsync(descriptor.Id, Token)).Values);
        var plan = new PpmsExperimentPlanImporter().Parse(MeterJson(new JsonObject { ["channel"] = "none", ["meter_range_v"] = 0.02 }), runtime.CreateImportBindings());
        Assert.Empty(plan.Preparations); var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(plan, Token);
        var row = Assert.Single(runs.LatestDataSet!.Rows); Assert.Equal(new(0.001, PhysicalUnit.Volt), row.Values[new("ppms.root.value")]);
        Assert.Equal(new(0.1, PhysicalUnit.Volt), Assert.Single(row.Quality).MeterConfiguration!.ManualRange);
        Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); Assert.False(runs.HasActiveRun);
        var auto = new PpmsExperimentPlanImporter().Parse(MeterJson(new JsonObject { ["channel"] = "none" }), runtime.CreateImportBindings()); await runs.StartPlanAsync(auto, Token);
        Assert.True(Assert.Single(Assert.Single(runs.LatestDataSet.Rows).Quality).MeterConfiguration!.AutoRange);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PpmsVoltageUsesActualWindowAndRepeatOnlyWhenFilterIsEnabled(bool enabled)
    {
        var b = new Bench(); b.Meter.Settings = new(1, null, 3, true, 7, AveragingMode.Moving, 0.7); b.Meter.Settings = new(2, new(1, PhysicalUnit.Volt), 3, true, 17, AveragingMode.Moving, 2);
        var plan = new PpmsExperimentPlanImporter().Parse(MeterJson(new JsonObject
        {
            ["instrument"] = "meter",
            ["channel"] = "none",
            ["meter_range_v"] = "0.02",
            ["nplc"] = 2,
            ["filter_enabled"] = enabled,
            ["filter_count"] = 4,
            ["filter_window_percent"] = 0.5
        }),
            new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["meter"] = b.Meter.Descriptor.Id }, [], standaloneVoltageMeters: [b.Meter.Descriptor.Id]));
        Assert.Empty(plan.Preparations); Assert.DoesNotContain(plan.Metadata.Keys, k => k.Contains("unused_filter", StringComparison.Ordinal));
        await b.Runs.StartPlanAsync(plan, Token); var settings = Assert.Single(Assert.Single(b.Runs.LatestDataSet!.Rows).Quality).MeterConfiguration!;
        Assert.Equal(1, settings.Channel); Assert.Equal(new(0.1, PhysicalUnit.Volt), settings.ManualRange); Assert.Equal(2, settings.Nplc); Assert.Equal(enabled, settings.FilterEnabled);
        Assert.Equal(enabled ? 4 : 7, settings.FilterCount); Assert.Equal(enabled ? AveragingMode.Repeat : AveragingMode.Moving, settings.FilterMode); Assert.Equal(enabled ? 0.5 : 0.7, settings.FilterWindowPercent);
    }
    [Theory]
    [InlineData("filter_count", "1")]
    [InlineData("filter_count", "2.5")]
    [InlineData("filter_window_percent", "11")]
    [InlineData("nplc", "51")]
    [InlineData("sense_channel", "2")]
    [InlineData("sensing_mode", "\"4wire\"")]
    [InlineData("channel", "\"ch1\"")]
    [InlineData("meter_range_v", "121")]
    [InlineData("meter_range_a", "1")]
    public void InvalidPpmsMeterOptionsFailAtImportWithPathWithoutConstructingDrivers(string field, string json)
    {
        var p = new JsonObject { ["instrument"] = "meter", ["channel"] = "none" }; p[field] = JsonNode.Parse(json);
        var bindings = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["meter"] = new("meter") }, [], standaloneVoltageMeters: [new("meter")]);
        var error = Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(MeterJson(p), bindings)); Assert.Contains("ppms.root", error.Message);
    }
    [Fact]
    public void PpmsFilterTimingAndCurrentCapabilityRejectRatherThanImitatingASourceMeter()
    {
        var bindings = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["meter"] = new("meter") }, [], standaloneVoltageMeters: [new("meter")]);
        Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(MeterJson(new JsonObject { ["instrument"] = "meter", ["filter_enabled"] = true, ["nplc"] = 50, ["filter_count"] = 17 }), bindings));
        Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(MeterJson(new JsonObject { ["instrument"] = "meter" }, "measure_current"), bindings));
        Assert.Throws<ArgumentException>(() => new ExperimentImportBindings([], [], standaloneVoltageMeters: [new("unbound")]));
    }
    [Theory]
    [InlineData("visa")]
    [InlineData("serial")]
    [InlineData("tcp")]
    public void EnabledPhysicalMeterRejectsWholeCatalogBeforeConstruction(string transport)
    {
        DeviceConnection connection = transport switch { "visa" => new VisaDeviceConnection("GPIB0::7::INSTR"), "serial" => new SerialDeviceConnection("test-port", TimeSpan.FromSeconds(2)), _ => new TcpDeviceConnection("127.0.0.1", 12345, "\n", TimeSpan.FromSeconds(2)) };
        var c = new DeviceConfigurationCatalog(null, [new(new("sim"), "K2182", "Simulation", ConfiguredDeviceCategory.Meter, new SimulationDeviceConnection()), new(new("physical"), "K2182", "Unsupported transport", ConfiguredDeviceCategory.Meter, connection)], []);
        var factory = new CountingFactory(); Assert.Throws<NotSupportedException>(() => new ConfiguredDeviceRuntime(c, factory, new(TimeSpan.FromSeconds(5)), new())); Assert.Equal(0, factory.Created);
        var disabled = new DeviceConfigurationCatalog(null, [c.Devices[0], new(c.Devices[1].Id, "K2182", "Offline hardware template", ConfiguredDeviceCategory.Meter, connection, false)], []);
        var runtime = new ConfiguredDeviceRuntime(disabled, factory, new(TimeSpan.FromSeconds(5)), new()); Assert.Single(runtime.Registry.Devices); Assert.Empty(runtime.CreateImportBindings().StandaloneVoltageMeters);
    }
    [Theory]
    [InlineData("channel")]
    [InlineData("filterCount")]
    [InlineData("filterMode")]
    [InlineData("missingNplc")]
    [InlineData("mixedStatus")]
    [InlineData("wrongInputVoltage")]
    [InlineData("extra")]
    public async Task DamagedMeterEvidenceCannotBeExportedAsConfirmedConfiguration(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "meter-damaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            var b = new Bench(); await b.Runs.StartPlanAsync(b.Plan(b.Read()), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var archive = new FileRunArchive(folder))
            {
                await archive.OnStartedAsync(snapshot with { State = Core.Measurements.MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
                await archive.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token);
            }
            var path = Path.Combine(folder, b.Runs.LatestDataSet!.Id.Value.ToString("N"), "rows.ndjson"); var row = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); var quality = row["quality"]![0]!;
            var meter = quality["meterConfiguration"]!.AsObject();
            switch (damage)
            {
                case "channel": meter["channel"] = 3; break;
                case "filterCount": meter["filterCount"] = 0; break;
                case "filterMode": meter["filterMode"] = "Unknown"; break;
                case "missingNplc": meter.Remove("nplc"); break;
                case "mixedStatus": quality["sourceStatus"] = 0; quality["measurementStatus"] = 0; break;
                case "wrongInputVoltage": meter["channel"] = 2; row["values"]!["read.voltage"]!["value"] = 13; break;
                case "extra": meter["unsupportedFunction"] = "ratio"; break;
            }
            await File.WriteAllTextAsync(path, row.ToJsonString() + "\n"); await using var fresh = new FileRunArchive(folder);
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token));
            var csv = Path.Combine(folder, "bad.csv"); await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(b.Runs.LatestDataSet.Id, csv, Token)); Assert.False(File.Exists(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static string MeterJson(JsonObject p, string name = "measure_voltage") => new JsonObject { ["type"] = "measurement", ["name"] = name, ["params"] = p }.ToJsonString();
    private sealed class CountingFactory : IConfiguredDeviceFactory
    {
        private readonly ConfiguredDeviceFactory inner = new(); public int Created { get; private set; }
        public void Validate(DeviceConfiguration configuration) => inner.Validate(configuration);
        public ConfiguredDevicePorts Create(DeviceConfiguration configuration) { Created++; return inner.Create(configuration); }
    }
}

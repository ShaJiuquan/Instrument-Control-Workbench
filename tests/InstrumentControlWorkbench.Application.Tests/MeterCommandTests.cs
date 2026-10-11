using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class MeterCommandTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private readonly MockKeithley2182Meter meter = new(new("meter"), "Two-input meter");
    private readonly NotificationHub hub = new();
    private readonly DeviceRegistry registry;
    public MeterCommandTests() => registry = new([new Keithley2182MeterSession(meter, hub)], new InstrumentLeaseService(TimeSpan.FromSeconds(5)));
    private ExperimentPlan Import(string name, JsonObject parameters) => new PpmsExperimentPlanImporter().Parse(new JsonObject
    { ["type"] = "command", ["name"] = name, ["source_line"] = 23, ["params"] = parameters }.ToJsonString(),
        new(new Dictionary<string, InstrumentId> { ["k2182"] = meter.Descriptor.Id }, [], standaloneVoltageMeters: [meter.Descriptor.Id]));
    [Fact]
    public async Task PublishedCommandsFixtureUsesConfiguredAliasesAndRetainsLaterSourceMeterEvidence()
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "k6221-meter-devices.json"), Token);
        var sourcePlan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "k6221-current-resistance.plan.json"), Token);
        var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        var imported = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-device-commands-import.json"), runtime.CreateImportBindings(currentSourcePreparations: sourcePlan.CurrentSourcePreparations), Token);
        Assert.Empty(imported.CurrentSourcePreparations);
        var read = new ReadStep(new("meter_read"), [new(new("meter_voltage"), new(new("nanovoltmeter"), Keithley2182Parameters.Voltage.Id))]);
        var plan = new ExperimentPlan("Configured instruments", new SequenceStep(new("fixture"), [imported.Root, read, sourcePlan.Root]), currentSourcePreparations: sourcePlan.CurrentSourcePreparations);
        var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(plan, Token);
        var rows = runs.LatestDataSet!.Rows; Assert.Equal(2, rows.Count);
        var meterSettings = Assert.Single(rows[0].Quality).MeterConfiguration!;
        Assert.Equal(2, meterSettings.Channel); Assert.Equal(new(1, PhysicalUnit.Volt), meterSettings.ManualRange); Assert.Equal(3, meterSettings.FilterCount); Assert.Equal(AveragingMode.Repeat, meterSettings.FilterMode); Assert.Equal(0.7, meterSettings.FilterWindowPercent);
        Assert.Equal(new(0.005, PhysicalUnit.Second), Assert.Single(rows[1].IndependentResistance).SourceConfiguration.SourceDelay);
        Assert.Equal(meterSettings, Assert.Single(rows[1].Quality, q => q.MeterConfiguration is not null).MeterConfiguration); Assert.False(runs.HasActiveRun);
        Assert.True((await runtime.Registry.GetStatusAsync(new("current-source"), Token)).SafetyConfirmed);
        await runtime.Registry.WriteAsync(new("nanovoltmeter"), Keithley2182Parameters.Channel.Id, new(1, PhysicalUnit.Dimensionless), Token);
        Assert.Equal(new(0.1, PhysicalUnit.Volt), await runtime.Registry.ReadAsync(new("nanovoltmeter"), Keithley2182Parameters.ManualRange.Id, Token));
        Assert.Equal(new((int)AveragingMode.Moving, PhysicalUnit.Dimensionless), await runtime.Registry.ReadAsync(new("nanovoltmeter"), Keithley2182Parameters.FilterMode.Id, Token));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterPartialCommandsRetainTargetInputControlsAndSharedIntegration(bool nested)
    {
        await meter.ConnectAsync(Token);
        var one = new Keithley2182Settings(1, new(100, PhysicalUnit.Volt), 3, false, 4, AveragingMode.Moving, 0.4);
        var two = new Keithley2182Settings(2, new(10, PhysicalUnit.Volt), 3, false, 8, AveragingMode.Repeat, 0.8);
        await meter.ConfigureAsync(one, Token); await meter.ConfigureAsync(two, Token); await meter.SelectChannelAsync(1, Token);
        var config = new JsonObject { ["filter_enabled"] = true };
        var p = nested ? new JsonObject { ["channel"] = 2, ["config"] = config } : config;
        if (!nested) p["channel"] = 2;
        var runs = new MeasurementRunService(registry, hub); await runs.StartPlanAsync(Import("k2182.set_filter", p), Token);
        Assert.Equal(new Keithley2182Settings(2, two.ManualRange, 3, true, 8, AveragingMode.Repeat, 0.8), (await meter.GetSnapshotAsync(Token)).Settings);
        Assert.Empty(runs.LatestDataSet!.Rows); Assert.False(runs.HasActiveRun);
        await meter.SelectChannelAsync(1, Token); Assert.Equal(one, (await meter.GetSnapshotAsync(Token)).Settings);
        await runs.StartPlanAsync(Import("k2182.set_nplc", new() { ["nplc"] = 5 }), Token);
        await meter.SelectChannelAsync(2, Token); Assert.Equal(5, (await meter.GetSnapshotAsync(Token)).Settings!.Nplc);
        Assert.True((await meter.GetSnapshotAsync(Token)).Settings!.FilterEnabled);
    }
    [Theory]
    [InlineData("range_v", 1)]
    [InlineData("range", 2)]
    public async Task OmittedChannelRangeSelectorUsesActuallySelectedInput(string key, int channel)
    {
        await meter.ConnectAsync(Token); await meter.SelectChannelAsync(channel, Token);
        var runs = new MeasurementRunService(registry, hub); await runs.StartPlanAsync(Import("k2182.set_range", new() { [key] = 0.005 }), Token);
        var s = (await meter.GetSnapshotAsync(Token)).Settings!; Assert.Equal(channel, s.Channel); Assert.Equal(new(channel == 1 ? 0.01 : 0.1, PhysicalUnit.Volt), s.ManualRange);
    }
    [Fact]
    public async Task ConfiguredInputsAndLaterNativeReadsSurviveFreshArchiveAndReplay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "meter-commands-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs"));
            var nodes = new JsonArray();
            foreach (var channel in new[] { 1, 2 })
            {
                nodes.Add(new JsonObject { ["type"] = "command", ["name"] = "k2182.set_range", ["params"] = new JsonObject { ["channel"] = channel, ["range"] = channel == 1 ? 0.005 : 0.2 } });
                nodes.Add(new JsonObject { ["type"] = "command", ["name"] = "k2182.set_filter", ["params"] = new JsonObject { ["channel"] = channel, ["enabled"] = true, ["filter_count"] = channel + 2, ["type"] = channel == 1 ? "MOV" : "REP", ["filter_window"] = channel * 0.5 } });
            }
            var imported = new PpmsExperimentPlanImporter().Parse(new JsonObject { ["type"] = "sequence", ["children"] = nodes }.ToJsonString(),
                new(new Dictionary<string, InstrumentId> { ["k2182"] = meter.Descriptor.Id }, [], standaloneVoltageMeters: [meter.Descriptor.Id]));
            var children = ((SequenceStep)imported.Root).Children;
            var steps = new List<ExperimentStep>();
            for (var i = 0; i < 2; i++)
            {
                steps.Add(children[2 * i]); steps.Add(children[2 * i + 1]);
                steps.Add(new ReadStep(new("read" + i), [new(new("voltage"), new(meter.Descriptor.Id, Keithley2182Parameters.Voltage.Id))]));
            }
            var plan = new ExperimentPlan("Configured two inputs", new SequenceStep(new("root"), steps)); var document = new JsonExperimentPlanDocument();
            plan = document.Parse(document.Serialize(plan)); var runs = new MeasurementRunService(registry, hub, [archive]); await runs.StartPlanAsync(plan, Token);
            var data = runs.LatestDataSet!; Assert.Equal(2, data.Rows.Count); Assert.Equal(new[] { 1, 2 }, data.Rows.Select(r => Assert.Single(r.Quality).MeterConfiguration!.Channel));
            Assert.Equal(new[] { 0.01, 1.0 }, data.Rows.Select(r => Assert.Single(r.Quality).MeterConfiguration!.ManualRange!.Value.Value));
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token);
            Assert.Equal(data.Rows.SelectMany(r => r.Quality), loaded.DataSet.Rows.SelectMany(r => r.Quality));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "out.csv"), Token); Assert.Contains("meterConfiguration", await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")));
            var otherHub = new NotificationHub(); var otherMeter = new MockKeithley2182Meter(meter.Descriptor.Id, "Two-input meter");
            var otherRegistry = new DeviceRegistry([new Keithley2182MeterSession(otherMeter, otherHub)], new InstrumentLeaseService(TimeSpan.FromSeconds(5)));
            var replay = new MeasurementRunService(otherRegistry, otherHub); await replay.StartPlanAsync(loaded.DataSet.Plan!, Token);
            Assert.Equal(data.Rows.Select(r => r.Values[new("voltage")]), replay.LatestDataSet!.Rows.Select(r => r.Values[new("voltage")]));
            Assert.Equal(data.Rows.SelectMany(r => r.Quality).Select(q => q.MeterConfiguration), replay.LatestDataSet.Rows.SelectMany(r => r.Quality).Select(q => q.MeterConfiguration));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("k2182.set_range", "{\"channel\":2,\"range\":100}")]
    [InlineData("k2182.set_range", "{\"channel\":258,\"range\":1}")]
    [InlineData("k2182.set_range", "{\"channel\":1.5,\"range\":1}")]
    [InlineData("k2182.set_range", "{\"range\":1,\"range_v\":2}")]
    [InlineData("k2182.set_range", "{\"range\":0}")]
    [InlineData("k2182.set_nplc", "{\"nplc\":51}")]
    [InlineData("k2182.set_filter", "{\"enabled\":true,\"count\":1}")]
    [InlineData("k2182.set_filter", "{\"enabled\":true,\"count\":2.5}")]
    [InlineData("k2182.set_filter", "{\"enabled\":true,\"window\":11}")]
    [InlineData("k2182.set_filter", "{\"enabled\":true,\"filter_type\":\"moving\"}")]
    [InlineData("k2182.set_filter", "{\"enabled\":true,\"config\":{\"enabled\":false}}")]
    [InlineData("k2182.set_filter", "{\"config\":{\"enabled\":true,\"channel\":2}}")]
    [InlineData("k2182.set_filter", "{\"enabled\":true,\"filter_enabled\":false}")]
    public async Task MalformedCommandsFailBeforeConnectionOrData(string command, string text)
    {
        var runs = new MeasurementRunService(registry, hub);
        var error = Assert.ThrowsAny<Exception>(() => runs.PreviewPlan(Import(command, JsonNode.Parse(text)!.AsObject())));
        if (error is FormatException) Assert.Contains("source line 23", error.Message);
        Assert.False((await meter.GetSnapshotAsync(Token)).Connected); Assert.Null(runs.LatestDataSet); Assert.False(runs.HasActiveRun);
    }
    [Fact]
    public async Task DisableIgnoresUnusedControlsButRetainsTheObservedInputProfile()
    {
        await meter.ConnectAsync(Token); var actual = new Keithley2182Settings(1, new(10, PhysicalUnit.Volt), 2, true, 8, AveragingMode.Moving, 0.8);
        await meter.ConfigureAsync(actual, Token); var runs = new MeasurementRunService(registry, hub);
        await runs.StartPlanAsync(Import("k2182.set_filter", new() { ["enabled"] = false, ["count"] = 65536, ["window"] = 500 }), Token);
        Assert.Equal(actual.WithParameter(Keithley2182Parameters.FilterEnabled.Id, new(0, PhysicalUnit.Dimensionless)), (await meter.GetSnapshotAsync(Token)).Settings);
    }
    [Fact]
    public async Task WrongActuallySelectedInputRangeFailsWithoutChangingItsProfile()
    {
        await meter.ConnectAsync(Token); await meter.SelectChannelAsync(2, Token); var actual = (await meter.GetSnapshotAsync(Token)).Settings;
        var runs = new MeasurementRunService(registry, hub); var plan = Import("k2182.set_range", new() { ["range"] = 100 }); runs.PreviewPlan(plan);
        await Assert.ThrowsAsync<ArgumentException>(() => runs.StartPlanAsync(plan, Token)); Assert.Equal(actual, (await meter.GetSnapshotAsync(Token)).Settings); Assert.Empty(runs.LatestDataSet!.Rows); Assert.False(runs.HasActiveRun);
    }
}

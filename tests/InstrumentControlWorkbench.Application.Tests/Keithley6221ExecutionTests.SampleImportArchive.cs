using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData(SampleTopology.TwoWire, "meas_r_2w", 1)]
    [InlineData(SampleTopology.FourWire, "meas_r_4w", 1)]
    [InlineData(SampleTopology.HallBar, "meas_r_hallbar", 3)]
    [InlineData(SampleTopology.VanDerPauw, "meas_r_vdp", 9)]
    public async Task ImportedIndependentSampleAliasesUseFrozenTopologyAndOwnEveryOutputCycle(SampleTopology topology, string name, int count)
    {
        await using var b = new Bench(columns: 6);
        var bindings = BindCurrentChannels(b, IndependentSampleCatalog(topology));
        var node = SampleJson(name, new() { ["sample"] = "sample1", ["meas_mode"] = "curr_dc", ["excitation_a"] = "-1e-5", ["compliance"] = 100, ["range_a"] = "12e-6", ["k2182_channel"] = 1, ["sense_mode"] = "2wire", ["settle_s"] = 0 });
        var plan = new PpmsExperimentPlanImporter().Parse(node, bindings); await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(count, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(count == 1 ? 1 : count - 1, b.Source.Enables);
        Assert.All(b.Runs.LatestDataSet.Rows.Where(r => r.Analysis is null), row =>
        {
            var evidence = Assert.Single(row.IndependentResistance); Assert.Equal(-100, evidence.Resistance.Value, 8);
            Assert.Equal(-1e-5, evidence.Current.Value); Assert.Equal(new(20e-6, PhysicalUnit.Ampere), evidence.SourceConfiguration.ManualRange);
            Assert.Equal(b.Meter.Descriptor.Id, row.Routing!.VoltageMeter); Assert.Equal(3, row.Environment.Count);
        });
        if (topology is SampleTopology.HallBar or SampleTopology.VanDerPauw)
            Assert.Equal(topology == SampleTopology.HallBar ? -50 : 100 * Math.PI / Math.Log(2), b.Runs.LatestDataSet.Rows[^1].Values[new("ppms.root.SheetResistance")].Value, 8);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Theory]
    [InlineData("source_managed", "true")]
    [InlineData("sense_channel", "2")]
    [InlineData("channel", "\"none\"")]
    [InlineData("sample_id", "\"missing\"")]
    [InlineData("meter_instrument", "\"k6221\"")]
    [InlineData("reference_instrument", "\"k2182\"")]
    [InlineData("current_meter_instrument", "\"k6221\"")]
    public async Task IndependentSamplePolicyViolationsHaveNodeDiagnosticsBeforeIo(string field, string value)
    {
        await using var b = new Bench(columns: 6); var p = new JsonObject { ["sample_id"] = "sample1", ["settle_s"] = 0 }; p[field] = JsonNode.Parse(value);
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(SampleJson("meas_r_4w", p), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.FourWire))));
        Assert.Contains("ppms.root", error.Message); Assert.Equal(0, b.Source.Connections); Assert.False((await b.Meter.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task ParentCurrentScanCannotConvertSampleIntoManagedRouting()
    {
        await using var b = new Bench(columns: 6); var node = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5), ["channel"] = "none", ["settle_s"] = 0 },
            ["children"] = new JsonArray(JsonNode.Parse(SampleJson("meas_r_4w", new() { ["sample_id"] = "sample1" })))
        };
        Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.FourWire))));
        Assert.Equal(0, b.Source.Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeIndependentSamplesAlsoSupportExplicitK2450CurrentExcitation(bool measured)
    {
        await using var b = new Bench(columns: 6); var original = IndependentSamplePlan(b, SampleTopology.HallBar); var group = Assert.IsType<SampleAnalysisStep>(original.Root);
        var source = new MockKeithley2450SourceMeter(b.Source.Descriptor.Id, "Explicit SMU current source");
        var reads = group.Readings.Select(wrapper =>
        {
            var old = Assert.IsType<ReadCurrentResistanceStep>(wrapper.Acquisition);
            return new ReadSampleResistanceStep(new ReadCurrentResistanceStep(old.Id, old.Source, old.Meter, measured ? CurrentReference.MeasuredSourceReadback : CurrentReference.ProgrammedSetpoint,
                old.VoltageColumn, old.CurrentColumn, old.ResistanceColumn, old.MinimumCurrent, old.ExpectedProgrammedCurrent, old.RequiredRoute, old.EnvironmentReads), wrapper.Binding, wrapper.Settle);
        });
        var plan = new ExperimentPlan(original.Name, new SampleAnalysisStep(group.Id, reads, group.Columns),
            [new(source.Id, SourceFunction.Current, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.FourWire, 1, true))], routing: original.Routing);
        var registry = new DeviceRegistry([new InstrumentManager(source, b.Hub), new Keithley2182MeterSession(b.Meter, b.Hub), new MatrixSwitchSession(b.Matrix, b.Hub)], b.Leases);
        var runs = new MeasurementRunService(registry, b.Hub); await runs.StartPlanAsync(plan, Token);
        Assert.Equal(3, runs.LatestDataSet!.Rows.Count); Assert.All(runs.LatestDataSet.Rows[^1].IndependentResistance, e =>
        { Assert.Equal(CurrentSourceModel.Keithley2450, e.SourceConfiguration.Model); Assert.Equal(measured ? CurrentReference.MeasuredSourceReadback : CurrentReference.ProgrammedSetpoint, e.CurrentReference); });
        Assert.False((await source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.Equal(0, b.Source.Connections);
    }

    [Fact]
    public async Task PublishedFourTopologyFieldFixtureRoundtripsNativeWideArchiveAndCsv()
    {
        var folder = Path.Combine(Path.GetTempPath(), "independent-samples-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = new FileRunArchive(Path.Combine(folder, "runs")); var root = AppContext.BaseDirectory;
            var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(root, "devices", "k6221-sample-devices.json"), Token);
            var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
            var profiles = await new PpmsRoutingProfileImporter().LoadAsync(Path.Combine(root, "profiles", "channel_profiles.json"), Path.Combine(root, "profiles", "samples.json"), Path.Combine(root, "profiles", "instrument_ports.json"), Token);
            var preparation = new CurrentSourcePreparation(new("current-source"), new(new(1e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt)));
            var bindings = profiles.Bind(runtime.CreateImportBindings(currentSourcePreparations: [preparation]), new("matrix"),
                [KeyValuePair.Create(new WiringProfileId("k6221_k2182"), preparation.Device)], new("k6221_k2182"), wiringVoltageMeters: [KeyValuePair.Create(new WiringProfileId("k6221_k2182"), new InstrumentId("nanovoltmeter"))]);
            var imported = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(root, "plans", "ppms-current-samples-import.json"), bindings, Token);
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "sample.plan.json"), imported, Token);
            var plan = await codec.LoadAsync(Path.Combine(folder, "sample.plan.json"), Token); Assert.Equal(new InstrumentId("nanovoltmeter"), Assert.Single(plan.Routing!.Wirings).VoltageMeter);
            var runs = new MeasurementRunService(runtime.Registry, hub, [writer]); await runs.StartPlanAsync(plan, Token);
            var rows = runs.LatestDataSet!.Rows; Assert.Equal(30, rows.Count); Assert.Equal(4, rows.Count(r => r.Analysis is not null)); Assert.Equal(2, rows.Count(r => r.MeasurementGroup is not null));
            Assert.All(rows.Where(r => r.MeasurementGroup is not null), row => { Assert.Equal(12, row.IndependentResistance.Count); Assert.Equal(24, row.Quality.Count); Assert.Equal(12, row.Environment.Count); });
            Assert.All(rows.Where(r => r.Routing is not null), row => { Assert.Equal(new InstrumentId("nanovoltmeter"), row.Routing!.VoltageMeter); Assert.Single(row.IndependentResistance); Assert.Equal(3, row.Environment.Count); });
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(runs.LatestDataSet.Id, Token);
            Assert.Equal(30, stored.DataSet.Rows.Count); Assert.Equal(rows[^1].IndependentResistance, stored.DataSet.Rows[^1].IndependentResistance);
            await fresh.ExportCsvAsync(stored.DataSet.Id, Path.Combine(folder, "samples.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "samples.csv"));
            Assert.Contains("routing_json", csv); Assert.Contains("independent_resistance_json", csv); Assert.Contains("nanovoltmeter", csv);
            await runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(30, runs.LatestDataSet!.Rows.Count);
            Assert.True((await runtime.Registry.GetStatusAsync(preparation.Device, Token)).SafetyConfirmed); Assert.False(runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("missingSummaryEvidence")]
    [InlineData("reorderedSummaryEvidence")]
    [InlineData("missingRawEvidence")]
    [InlineData("missingRouteMeter")]
    [InlineData("wrongRouteMeter")]
    [InlineData("wrongInput")]
    public async Task DamagedIndependentSampleEvidenceRejectsReloadAndPreservesPreviousExport(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "sample-damaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var b = new Bench(columns: 6); await b.Runs.StartPlanAsync(IndependentSamplePlan(b, SampleTopology.HallBar), Token);
            var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
                foreach (var row in snapshot.Rows) await writer.OnRowAsync(snapshot.Id, row, Token);
            }
            var path = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson");
            var lines = (await File.ReadAllLinesAsync(path)).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            switch (damage)
            {
                case "missingSummaryEvidence": lines[2].Remove("independentResistance"); break;
                case "reorderedSummaryEvidence": var evidence = lines[2]["independentResistance"]!.AsArray(); var first = evidence[0]!.DeepClone(); evidence[0] = evidence[1]!.DeepClone(); evidence[1] = first; break;
                case "missingRawEvidence": lines[0].Remove("independentResistance"); break;
                case "missingRouteMeter": lines[0]["routing"]!.AsObject().Remove("voltageMeter"); break;
                case "wrongRouteMeter": lines[0]["routing"]!["voltageMeter"] = "source"; break;
                case "wrongInput": lines[0]["quality"]![1]!["meterConfiguration"]!["channel"] = 2; break;
            }
            await File.WriteAllLinesAsync(path, lines.Select(line => line.ToJsonString())); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token)); var csv = Path.Combine(folder, "previous.csv"); await File.WriteAllTextAsync(csv, "previous accepted results");
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("previous accepted results", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static string SampleJson(string name, JsonObject p) => new JsonObject { ["type"] = "measurement", ["name"] = name, ["params"] = p }.ToJsonString();
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Fact]
    public async Task PublishedRecoverablePlanKeepsHealthyValuesAndRoundTripsExplicitContinuationPolicy()
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "k6221-meter-devices.json"), Token);
        var doc = new JsonExperimentPlanDocument(); var plan = await doc.LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "current-resistance-invalid-points.plan.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub); var runs = new MeasurementRunService(runtime.Registry, hub);
        await runs.StartPlanAsync(doc.Parse(doc.Serialize(plan)), Token); var row = Assert.Single(runs.LatestDataSet!.Rows);
        Assert.Empty(row.AcquisitionFailures); Assert.Single(row.IndependentResistance); Assert.Equal(1, row.IndependentResistance[0].Attempts);
        Assert.Contains("continueOnAcquisitionFailure", doc.Serialize(runs.LatestDataSet.Plan!)); Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public async Task PartialAnalysisAndWidePointSurviveFreshNativeAndArchiveReplayAndCsv()
    {
        var folder = Path.Combine(Path.GetTempPath(), "invalid-point-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive, columns: 6);
            var plan = new PpmsExperimentPlanImporter().Parse(SampleJson("meas_r_hallbar", new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.HallBar)));
            plan = new(plan.Name, new MeasurementGroupStep(new("point"), [new("Hall", plan.Root), new("Independent reading", RecoveryPlan(b).Root)]), routing: plan.Routing, currentSourcePreparations: plan.CurrentSourcePreparations);
            b.Meter.OnRead = _ => (b.Meter.ReadCalls - 1) % 3 is 1 or 2 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "Point input absent")) : Task.CompletedTask;
            var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); await b.Runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var original = b.Runs.LatestDataSet!.Capture(); Assert.Equal(5, original.Rows.Count); Assert.Equal(2, original.Rows[^1].AcquisitionFailures.Count);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(original.Id, Token);
            for (var i = 0; i < original.Rows.Count; i++) { Assert.Equal(original.Rows[i].AcquisitionFailures, loaded.DataSet.Rows[i].AcquisitionFailures); Assert.Equal(original.Rows[i].Values, loaded.DataSet.Rows[i].Values); }
            await fresh.ExportCsvAsync(original.Id, Path.Combine(folder, "points.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "points.csv"));
            Assert.Contains("acquisition_failures_json", csv); Assert.Contains("Point input absent", csv); Assert.Contains("overrange", csv);
            await b.Runs.StartPlanAsync(loaded.DataSet.Plan!, Token); Assert.Equal(5, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(2, b.Runs.LatestDataSet.Rows[^1].AcquisitionFailures.Count);
            Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("status")]
    [InlineData("kind")]
    [InlineData("time")]
    [InlineData("off")]
    [InlineData("excitation")]
    [InlineData("values")]
    [InlineData("summary")]
    [InlineData("math")]
    [InlineData("input")]
    public async Task ForgedInvalidPointOrPartialSummaryRejectsReloadWithoutReplacingCsv(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "invalid-point-damaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var b = new Bench(columns: 6); b.Meter.OnRead = _ => b.Meter.ReadCalls == 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.InvalidResponse, "Absent input")) : Task.CompletedTask;
            var plan = new PpmsExperimentPlanImporter().Parse(SampleJson("meas_r_hallbar", new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.HallBar)));
            await b.Runs.StartPlanAsync(plan, Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
                foreach (var row in snapshot.Rows) await writer.OnRowAsync(snapshot.Id, row, Token);
            }
            var file = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson"); var lines = (await File.ReadAllLinesAsync(file)).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray(); var failure = lines[0]["acquisitionFailures"]![0]!.AsObject();
            switch (damage)
            {
                case "missing": lines[0].Remove("acquisitionFailures"); break;
                case "identity": failure["meter"] = "source"; break;
                case "status": failure["status"] = "ok"; break;
                case "kind": failure["kind"] = "99"; break;
                case "time": failure["idleConfirmedAt"] = DateTimeOffset.UtcNow.AddYears(1); break;
                case "off": failure["sourceOffConfirmedAt"] = null; break;
                case "excitation": failure["sourceConfiguration"]!["level"] = 2e-5; break;
                case "values": lines[0]["values"]![failure["voltageColumn"]!.GetValue<string>()] = new JsonObject { ["value"] = 0, ["unit"] = "Volt" }; break;
                case "summary": lines[2].Remove("acquisitionFailures"); break;
                case "math": lines[2]["analysis"]!["issues"] = "None"; break;
                case "input": failure["meterSettings"]!["channel"] = 2; break;
            }
            await File.WriteAllLinesAsync(file, lines.Select(l => l.ToJsonString())); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token)); var csv = Path.Combine(folder, "accepted.csv"); await File.WriteAllTextAsync(csv, "accepted results");
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("accepted results", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

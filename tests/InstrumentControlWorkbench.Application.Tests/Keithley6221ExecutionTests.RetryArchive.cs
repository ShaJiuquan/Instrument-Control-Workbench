using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
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
    [Fact]
    public async Task PublishedRetryPlanRunsHealthyAcquisitionWithoutInventingAFailedAttempt()
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "k6221-meter-devices.json"), Token);
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "current-resistance-retry.plan.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub); var runs = new MeasurementRunService(runtime.Registry, hub);
        await runs.StartPlanAsync(plan, Token);
        var evidence = Assert.Single(Assert.Single(runs.LatestDataSet!.Rows).IndependentResistance);
        Assert.Null(evidence.Retry); Assert.Equal(1, evidence.Attempts); Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public async Task LiveTimedOutRetryAbortRemainsAttachedWithoutOverlappingReadOrRecovery()
    {
        await using var b = new Bench(); var owner = RunId.New();
        var resources = await b.Registry.AcquireRunAsync([b.Source.Descriptor.Id, b.Meter.Descriptor.Id], owner, Token, TimeSpan.FromMilliseconds(40));
        await resources.ConnectAllAsync(Token); await resources.ConfigureCurrentSourceAsync(b.Source.Descriptor.Id, b.Preparation.Settings, Token); await resources.EnableSourceOutputAsync(b.Source.Descriptor.Id, Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Terminal failed read");
        b.Meter.OnAbort = async _ => { try { await release.Task; } finally { completed.TrySetResult(); } };
        var r = b.Read; var read = new ReadCurrentResistanceStep(r.Id, r.Source, r.Meter, r.CurrentReference, r.VoltageColumn, r.CurrentColumn, r.ResistanceColumn, r.MinimumCurrent, r.ExpectedProgrammedCurrent, retry: new(true, TimeSpan.Zero));
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => resources.ReadCurrentResistanceAsync(read, b.Preparation.Settings.Current, Token));
            Assert.Equal(1, b.Meter.ReadCalls); Assert.Equal(1, b.Meter.AbortCalls); Assert.Equal(1, b.Source.Enables);
            Assert.NotEmpty(await resources.StopAllAsync(Token, retryFailed: true)); Assert.Equal(1, b.Meter.AbortCalls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
            Assert.All(resources.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        }
        finally
        {
            b.Meter.OnAbort = null; release.TrySetResult(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(await resources.StopAllAsync(Token));
            Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true)); await resources.ReleaseAsync(Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshArchiveNativeReplayAndCsvKeepRetryEvidenceInRawAnalysisAndWidePoints(bool owned)
    {
        var folder = Path.Combine(Path.GetTempPath(), "current-retry-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive, columns: 6);
            var plan = owned ? new PpmsExperimentPlanImporter().Parse(SampleJson("meas_r_hallbar", new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.HallBar))) : RetryPlan(b, false);
            if (owned)
            {
                var member = plan.Root;
                plan = new(plan.Name, new MeasurementGroupStep(new("point"), [new("Hall", member), new("Voltage check", new ReadStep(new("voltage_check"), [new(new("check.V"), new(b.Meter.Descriptor.Id, Keithley2182Parameters.Voltage.Id))]))]), routing: plan.Routing, currentSourcePreparations: plan.CurrentSourcePreparations);
            }
            var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            b.Meter.OnRead = _ => (owned ? (b.Meter.ReadCalls - 1) % 5 is 0 or 2 : b.Meter.ReadCalls % 2 == 1) ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.CommandRejected, "Transient -221", -221)) : Task.CompletedTask;
            await b.Runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var original = b.Runs.LatestDataSet!.Capture(); Assert.Equal(owned ? 5 : 1, original.Rows.Count);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(original.Id, Token);
            for (var i = 0; i < original.Rows.Count; i++) Assert.Equal(original.Rows[i].IndependentResistance, stored.DataSet.Rows[i].IndependentResistance);
            Assert.All(stored.DataSet.Rows.SelectMany(r => r.IndependentResistance), e => Assert.Equal(2, e.Attempts));
            await fresh.ExportCsvAsync(original.Id, Path.Combine(folder, "rows.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "rows.csv"));
            Assert.Contains("Transient -221", csv); Assert.Contains("idleConfirmedAt", csv); Assert.Contains("attemptStartedAt", csv);
            await b.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(original.Rows.Count, b.Runs.LatestDataSet!.Rows.Count);
            Assert.All(b.Runs.LatestDataSet.Rows.SelectMany(r => r.IndependentResistance), e => Assert.Equal(2, e.Attempts));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("ownership")]
    [InlineData("kind")]
    [InlineData("code")]
    [InlineData("time")]
    [InlineData("summary")]
    [InlineData("meterSettings")]
    [InlineData("missingTime")]
    public async Task DamagedRetryEvidenceCannotReloadOrReplaceAcceptedCsv(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "current-retry-damaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var b = new Bench(columns: 6);
            b.Meter.OnRead = _ => b.Meter.ReadCalls % 2 == 1 ? Task.FromException(new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Transient")) : Task.CompletedTask;
            var plan = new PpmsExperimentPlanImporter().Parse(SampleJson("meas_r_hallbar", new() { ["sample_id"] = "sample1", ["mode"] = "curr_dc", ["settle_s"] = 0 }), BindCurrentChannels(b, IndependentSampleCatalog(SampleTopology.HallBar)));
            await b.Runs.StartPlanAsync(plan, Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [], Blackboard = null }, Token);
                foreach (var row in snapshot.Rows) await writer.OnRowAsync(snapshot.Id, row, Token);
            }
            var file = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson");
            var lines = (await File.ReadAllLinesAsync(file)).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray(); var retry = lines[0]["independentResistance"]![0]!["retry"]!.AsObject();
            switch (damage)
            {
                case "ownership": retry["sourceOffConfirmedAt"] = null; break;
                case "kind": retry["kind"] = "Overrange"; break;
                case "code": retry["kind"] = "CommandRejected"; retry["commandCode"] = 419; break;
                case "time": retry["idleConfirmedAt"] = DateTimeOffset.UtcNow.AddYears(1); break;
                case "meterSettings": retry["meterSettings"]!["nplc"] = 2; break;
                case "missingTime": retry.Remove("failedAt"); break;
                case "summary": lines[2]["independentResistance"]![0]!.AsObject().Remove("retry"); break;
            }
            await File.WriteAllLinesAsync(file, lines.Select(l => l.ToJsonString())); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token));
            var csv = Path.Combine(folder, "accepted.csv"); await File.WriteAllTextAsync(csv, "accepted results");
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("accepted results", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task TypedSnapshotFailureCannotEnterTheVoltageReadRetryBoundary()
    {
        await using var b = new Bench(); var refused = false;
        b.Meter.OnSnapshot = async _ =>
        {
            if (!refused && (await b.Source.GetSnapshotAsync(Token)).OutputEnabled) { refused = true; throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.ReadTimeout, "Snapshot failure"); }
        };
        await Assert.ThrowsAsync<InstrumentReadException>(() => b.Runs.StartPlanAsync(RetryPlan(b, true), Token));
        Assert.Equal(0, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables); Assert.False(b.Runs.HasActiveRun);
    }
}

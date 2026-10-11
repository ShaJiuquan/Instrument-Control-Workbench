using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    private static ExperimentPlan DelayCommand(Bench b, JsonObject p) => new PpmsExperimentPlanImporter().Parse(new JsonObject
    { ["type"] = "command", ["name"] = "k6221.set_source_delay", ["source_line"] = 19, ["params"] = p }.ToJsonString(),
        new(new Dictionary<string, InstrumentId> { ["k6221"] = b.Source.Descriptor.Id }, [], currentSourcePreparations: [b.Preparation]));
    [Theory]
    [InlineData("delay_s")]
    [InlineData("source_delay_s")]
    [InlineData("delay")]
    public async Task ImportedDelayRegisterDoesNotWaitOrChangeCurrentAndExistingPreparationsRetainIt(string key)
    {
        await using var b = new Bench(); await b.Source.ConnectAsync(Token);
        var actual = new Keithley6221Settings(new(3e-5, PhysicalUnit.Ampere), new(50, PhysicalUnit.Volt), new(200e-6, PhysicalUnit.Ampere));
        await b.Source.ConfigureAsync(actual, Token); var plan = DelayCommand(b, new() { [key] = 999999.999 }); Assert.Empty(plan.CurrentSourcePreparations);
        await b.Runs.StartPlanAsync(plan, Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Equal(actual.WithParameter(Keithley6221Parameters.SourceDelay.Id, new(999999.999, PhysicalUnit.Second)), (await b.Source.GetSnapshotAsync(Token)).Settings);
        await b.Runs.StartPlanAsync(b.Plan(false), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new(999999.999, PhysicalUnit.Second), Assert.Single(row.IndependentResistance).SourceConfiguration.SourceDelay);
        Assert.Equal(b.Preparation.Settings.Current.Value, Assert.Single(row.IndependentResistance).SourceConfiguration.Level);
        var source = b.Runs.LatestDataSet.Instruments.Single(i => i.InstrumentId == b.Source.Descriptor.Id);
        Assert.Equal(new(999999.999, PhysicalUnit.Second), source.Parameters.Single(p => p.Descriptor.Id == Keithley6221Parameters.SourceDelay.Id).Value);
    }
    [Theory]
    [InlineData("{\"delay_s\":0}")]
    [InlineData("{\"delay_s\":-1}")]
    [InlineData("{\"delay_s\":1000000}")]
    [InlineData("{\"delay_s\":1,\"delay\":2}")]
    public async Task InvalidSourceDelayFailsBeforeSourceConnection(string json)
    {
        await using var b = new Bench(); Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(DelayCommand(b, JsonNode.Parse(json)!.AsObject())));
        Assert.Equal(0, b.Source.Connections); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayEvidenceSurvivesNativeDocumentAndFreshArchiveIncludingInvalidPoint(bool invalid)
    {
        var folder = Path.Combine(Path.GetTempPath(), "source-delay-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive);
            var delay = new Quantity(0.005, PhysicalUnit.Second);
            var prep = new CurrentSourcePreparation(b.Source.Descriptor.Id, new(b.Preparation.Settings.Current, b.Preparation.Settings.Compliance, b.Preparation.Settings.ManualRange, delay));
            var basePlan = invalid ? RecoveryPlan(b) : b.Plan(false);
            if (invalid) b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "voltage overrange");
            var plan = new ExperimentPlan("Explicit source delay", basePlan.Root, currentSourcePreparations: [prep]); var doc = new JsonExperimentPlanDocument();
            var text = doc.Serialize(plan); Assert.Contains("sourceDelay", text); plan = doc.Parse(text); Assert.Equal(delay, Assert.Single(plan.CurrentSourcePreparations).Settings.SourceDelay);
            await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; var row = Assert.Single(data.Rows);
            Assert.Equal(delay, invalid ? Assert.Single(row.AcquisitionFailures).SourceConfiguration.SourceDelay : Assert.Single(row.IndependentResistance).SourceConfiguration.SourceDelay);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token);
            Assert.Equal(row.IndependentResistance, loaded.DataSet.Rows[0].IndependentResistance); Assert.Equal(row.AcquisitionFailures, loaded.DataSet.Rows[0].AcquisitionFailures);
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "out.csv"), Token); Assert.Contains("sourceDelay", await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")));
            Assert.DoesNotContain("sourceDelay", doc.Serialize(b.Plan(false)));
            Assert.Null(Assert.Single(doc.Parse(doc.Serialize(b.Plan(false))).CurrentSourcePreparations).Settings.SourceDelay);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayDriftDuringReadOrPauseDiscardsDataAndCannotRestoreOutput(bool paused)
    {
        await using var b = new Bench();
        if (!paused) b.Meter.OnRead = async token =>
        {
            var actual = (await b.Source.GetSnapshotAsync(token)).Settings!;
            await b.Source.ConfigureAsync(new(actual.Current, actual.Compliance, actual.ManualRange, new(0.002, PhysicalUnit.Second)), token);
        };
        using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(paused ? b.Plan(false, new DelayStep(new("wait"), TimeSpan.FromSeconds(30))) : b.Plan(false), Token);
        if (paused)
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            var state = (await b.Source.GetSnapshotAsync(Token)).Settings!; var enables = b.Source.Enables;
            await b.Source.ConfigureAsync(new(state.Current, state.Compliance, state.ManualRange, new(0.002, PhysicalUnit.Second)), Token);
            b.Runs.Resume(); await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Equal(enables, b.Source.Enables);
            Assert.Single(b.Runs.LatestDataSet!.Rows);
        }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Empty(b.Runs.LatestDataSet!.Rows); }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ManualDelayWriteIsOwnedDuringRunsAndRefusalRetainsSafetyClaims()
    {
        await using var b = new Bench(); await b.Registry.ConnectAsync(b.Source.Descriptor.Id, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Source.Descriptor.Id, Keithley6221Parameters.SourceDelay.Id, Token));
        await b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.SourceDelay.Id, new(0.001, PhysicalUnit.Second), Token);
        Assert.Equal(new(0.001, PhysicalUnit.Second), await b.Registry.ReadAsync(b.Source.Descriptor.Id, Keithley6221Parameters.SourceDelay.Id, Token));
        using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(false, new DelayStep(new("wait"), TimeSpan.FromSeconds(30))), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.SourceDelay.Id, new(0.1, PhysicalUnit.Second), Token));
        b.Runs.Abort(); await running;
        b.Source.RefuseSettings = true; b.Source.RefuseStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(DelayCommand(b, new() { ["delay_s"] = 0.1 }), Token));
        Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Source.Descriptor.Id, DateTimeOffset.UtcNow.AddYears(1)));
        b.Source.RefuseSettings = false; b.Source.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
}

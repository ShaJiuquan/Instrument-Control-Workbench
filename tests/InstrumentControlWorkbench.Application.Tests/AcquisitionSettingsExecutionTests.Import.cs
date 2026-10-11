using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class AcquisitionSettingsExecutionTests
{
    [Theory]
    [InlineData(SenseFunction.Current, true)]
    [InlineData(SenseFunction.Current, false)]
    [InlineData(SenseFunction.Voltage, true)]
    [InlineData(SenseFunction.Voltage, false)]
    public async Task PpmsMeterFilteringTargetsCorrectFunctionAndUsesRuntimeRepeatSemantics(SenseFunction function, bool enabled)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        peer.SetAcquisition(function, new(false, true, true, 17, AveragingMode.Moving));
        // Start from another function to prove the settings apply to the chosen meter.
        var prep = b.Preparation(function == SenseFunction.Current ? SenseFunction.Voltage : SenseFunction.Current);
        var p = new JsonObject { ["instrument"] = "k2450", ["sensing_mode"] = "4wire", ["channel"] = "none", ["filter_enabled"] = enabled, ["filter_count"] = 4 };
        if (function == SenseFunction.Voltage) p["filter_window_percent"] = 1.2;
        // The source output node is authored natively; the importer only needs the measurement node.
        var imported = new PpmsExperimentPlanImporter().Parse(new JsonObject { ["type"] = "measurement", ["name"] = function == SenseFunction.Voltage ? "measure_voltage" : "measure_current", ["params"] = p }.ToJsonString(), new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k2450"] = b.Source.Id }, [], [prep]));
        var plan = new ExperimentPlan("PPMS filter", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), imported.Root]), imported.Preparations, imported.Metadata);
        await b.Runs.StartPlanAsync(new JsonExperimentPlanDocument().Parse(new JsonExperimentPlanDocument().Serialize(plan)), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var actual = Assert.Single(row.Quality).AcquisitionConfiguration!;
        Assert.Equal(enabled, actual.AveragingEnabled); Assert.Equal(enabled ? 4 : 17, actual.AveragingCount); Assert.Equal(enabled ? AveragingMode.Repeat : AveragingMode.Moving, actual.AveragingMode);
        Assert.False(actual.AutoZero); Assert.True(actual.AutorangeRebound);
        Assert.Equal((int)function, (await b.Registry.ReadAsync(b.Source.Id, Keithley2450Parameters.ActiveSenseFunction.Id, Token)).Value);
        if (function == SenseFunction.Voltage) Assert.Equal(new(1.2, PhysicalUnit.Dimensionless), plan.Metadata["ppms.root.k2450_unused_filter_window_percent"]);
        Assert.DoesNotContain(peer.Server.Commands, c => c.Contains("WINDOW", StringComparison.Ordinal)); Assert.False(peer.Output); await b.Source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData("filter_count", 1)]
    [InlineData("filter_count", 4.5)]
    [InlineData("filter_count", 101)]
    [InlineData("filter_window_percent", -1)]
    [InlineData("filter_window_percent", 11)]
    [InlineData("nplc", 10)]
    public async Task InvalidPpmsFilterSettingsPreserveSourceLineAndRejectBeforeAnyConnection(string field, double value)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var prep = b.Preparation(SenseFunction.Voltage);
        var p = new JsonObject { ["instrument"] = "k2450", ["sensing_mode"] = "4wire", ["filter_enabled"] = true, ["filter_count"] = 30, [field] = value };
        var node = new JsonObject { ["type"] = "measurement", ["name"] = "measure_voltage", ["params"] = p }; node["source_line"] = 7;
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k2450"] = b.Source.Id }, [], [prep])));
        Assert.Contains("source line 7", error.Message); Assert.Equal(0, peer.Server.Connections); Assert.Empty(peer.Server.Commands);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOptionalProfileKeepsOldDocumentsUnspecifiedAndStrictlyRejectsPartialOrInvalidNewProfiles(bool partial)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var doc = new JsonExperimentPlanDocument(); var legacy = b.Plan(b.Preparation(SenseFunction.Current), b.Read(SenseFunction.Current));
        var legacyText = doc.Serialize(legacy); Assert.DoesNotContain("acquisition", JsonNode.Parse(legacyText)!["preparations"]![0]!["sense"]!.ToJsonString());
        Assert.Null(doc.Parse(legacyText).Preparations[0].Sense.Acquisition);
        var json = JsonNode.Parse(doc.Serialize(b.Plan(b.Preparation(SenseFunction.Current, new(false, true, true, 3, AveragingMode.Repeat)), b.Read(SenseFunction.Current))))!;
        var profile = json["preparations"]![0]!["sense"]!["acquisition"]!;
        if (partial) profile.AsObject().Remove("autoZero"); else profile["averagingCount"] = 3.5;
        Assert.NotNull(Record.Exception(() => doc.Parse(json.ToJsonString()))); Assert.Equal(0, peer.Server.Connections);
    }
    [Theory]
    [InlineData("count")]
    [InlineData("mode")]
    [InlineData("extra")]
    [InlineData("rebound")]
    public async Task RowProfileTamperingRejectsArchiveAndPreservesExistingCsv(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "profile-tamper-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var function = damage == "rebound" ? SenseFunction.Resistance : SenseFunction.Current;
            await b.Runs.StartPlanAsync(b.Plan(b.Preparation(function, new(false, function != SenseFunction.Resistance, true, 3, AveragingMode.Repeat)), b.Read(function)), Token);
            var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token); await archive.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token);
            }
            var path = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson"); var row = JsonNode.Parse(await File.ReadAllTextAsync(path))!; var settings = row["quality"]![0]!["acquisitionConfiguration"]!;
            if (damage == "count") settings["averagingCount"] = 0; else if (damage == "mode") settings["averagingMode"] = "bad"; else if (damage == "rebound") settings["autorangeRebound"] = true; else settings["unexpected"] = true;
            await File.WriteAllTextAsync(path, row.ToJsonString() + "\n"); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token));
            var csv = Path.Combine(folder, "old.csv"); await File.WriteAllTextAsync(csv, "keep"); await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("keep", await File.ReadAllTextAsync(csv)); await b.Source.DisconnectAsync(Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task PrecanceledRefreshPlanCannotConnectOrRefresh()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var plan = new ExperimentPlan("cancel refresh", new SetParameterStep(new("zero"), new(b.Source.Id, Keithley2450Parameters.RefreshZero.Id), new ConstantValue(new(1, PhysicalUnit.Dimensionless))), [b.Preparation(SenseFunction.Current)]);
        await b.Runs.StartPlanAsync(plan, canceled.Token); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Equal(0, peer.Server.Connections); Assert.Equal(0, peer.ZeroRefreshes);
    }
    [Fact]
    public async Task SimulationConfigurationDoesNotManufactureObservedBufferQuality()
    {
        var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub();
        var runs = new MeasurementRunService(new InstrumentManager(source, hub), new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        var profile = new Keithley2450AcquisitionSettings(false, true, true, 3, AveragingMode.Moving);
        var prep = new SourcePreparation(source.Id, SourceFunction.Voltage, new(0.02, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true, Acquisition: profile));
        var read = new ReadSourceSenseStep(new("read"), source.Id, SourceFunction.Voltage, new("V"), new("I"));
        await runs.StartPlanAsync(new("simulation", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), source.Id, true), read]), [prep]), Token);
        var row = Assert.Single(runs.LatestDataSet!.Rows); Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); Assert.All(row.Quality, q => { Assert.Null(q.Status); Assert.Equal(profile, q.AcquisitionConfiguration); });
    }
}

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
    [Theory]
    [InlineData("")]
    [InlineData("CH1")]
    [InlineData("contacts")]
    public async Task IvMeterInheritsFrozenChannelOrAcceptsEquivalentProfileAlias(string childChannel)
    {
        await using var b = new Bench(); var tree = IvMeterTree(childChannel);
        var codec = new JsonExperimentPlanDocument(); var plan = new PpmsExperimentPlanImporter().Parse(tree.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        Assert.Empty(plan.Preparations); Assert.Single(plan.CurrentSourcePreparations); Assert.NotNull(plan.Routing);
        plan = codec.Parse(codec.Serialize(plan)); Assert.Contains("requiredRoute", codec.Serialize(plan));
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { -1e-5, 0, 1e-5 }, rows.Select(r => r.Values[new("ppms.root.axis")].Value));
        Assert.All(rows, row =>
        {
            Assert.Equal(new(0.001, PhysicalUnit.Volt), row.Values[new("ppms.root.0.value")]);
            Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); Assert.Equal(2, Assert.Single(row.Quality).MeterConfiguration!.Nplc);
            Assert.Equal(new(0.1, PhysicalUnit.Volt), Assert.Single(row.Quality).MeterConfiguration!.ManualRange);
            Assert.Empty(row.IndependentResistance); Assert.Null(row.Routing);
        });
        Assert.Equal(3, b.Meter.ReadCalls); Assert.Equal(1, b.Source.Enables); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("CH2")]
    [InlineData("none")]
    [InlineData("missing")]
    public async Task IvMeterCannotSwitchOrBypassManagedChannel(string channel)
    {
        await using var b = new Bench(); var tree = IvMeterTree(channel);
        Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(tree.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog())));
        Assert.Equal(0, b.Source.Connections); Assert.False((await b.Meter.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task RoutedPassiveVoltageReadDoesNotApplyUnusedExcitationPreparation()
    {
        await using var b = new Bench(); var tree = (JsonObject)IvMeterTree("contacts")["children"]![0]!.DeepClone();
        var plan = new PpmsExperimentPlanImporter().Parse(tree.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        Assert.Empty(plan.Preparations); Assert.Empty(plan.CurrentSourcePreparations);
        await b.Runs.StartPlanAsync(plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(0, b.Source.Enables);
        Assert.Equal(1, b.Meter.ReadCalls); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IvDriftBeforeOrDuringReadDiscardsMeasurementWithoutBlackboardLeak(bool during)
    {
        await using var b = new Bench();
        if (during) b.Meter.OnRead = token => b.Matrix.OpenAsync([b.Route.Closed[0]], token);
        else b.Meter.OnSnapshot = async token =>
        {
            if ((await b.Source.GetSnapshotAsync(Token)).OutputEnabled) await b.Matrix.OpenAsync([b.Route.Closed[0]], Token);
        };
        var plan = new PpmsExperimentPlanImporter().Parse(IvMeterTree().ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(during ? 1 : 0, b.Meter.ReadCalls);
        Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("ppms.root.0.value")));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task IvAbortDrainsBlockedMeterAndConfirmsSourceOff()
    {
        await using var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var plan = new PpmsExperimentPlanImporter().Parse(IvMeterTree().ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        var run = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await run;
            Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows); Assert.False(b.Runs.HasActiveRun);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Source.LastStopTokenLive); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task IvReadFaultDoesNotRetryAndRetainsRefusedOutputStopUntilRecovery()
    {
        await using var b = new Bench(); b.Meter.OnRead = token => { b.Source.RefuseStop = true; throw new IOException("Meter read failed."); };
        var plan = new PpmsExperimentPlanImporter().Parse(IvMeterTree().ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Contains("Meter read failed", error.ToString());
        Assert.Equal(1, b.Meter.ReadCalls); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        b.Source.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task IvArchiveAndCsvRetainZeroPointAndGuardedNativePlan()
    {
        var folder = Path.Combine(Path.GetTempPath(), "iv-meter-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive);
            var plan = new PpmsExperimentPlanImporter().Parse(IvMeterTree().ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
            await b.Runs.StartPlanAsync(plan, Token); await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(b.Runs.LatestDataSet!.Id, Token);
            Assert.Equal(3, stored.DataSet.Rows.Count); Assert.Equal(0, stored.DataSet.Rows[1].Values[new("ppms.root.axis")].Value);
            Assert.Contains("requiredRoute", new JsonExperimentPlanDocument().Serialize(stored.DataSet.Plan!));
            Assert.Equal(b.Runs.LatestBlackboardSnapshot!.Entries[new("ppms.root.0.value")], stored.DataSet.Blackboard!.Entries[new("ppms.root.0.value")]);
            await reopened.ExportCsvAsync(stored.DataSet.Id, Path.Combine(folder, "iv.csv"), Token); Assert.Contains("ppms.root.0.value", await File.ReadAllTextAsync(Path.Combine(folder, "iv.csv")));
            await using var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(3, fresh.Meter.ReadCalls);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task PublishedIvFixtureUsesActualIndependentDeviceAndWiringFiles()
    {
        var root = AppContext.BaseDirectory;
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(root, "devices", "k6221-meter-matrix-devices.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
        var profiles = await new PpmsRoutingProfileImporter().LoadAsync(Path.Combine(root, "profiles", "channel_profiles.json"), Path.Combine(root, "profiles", "samples.json"), Path.Combine(root, "profiles", "instrument_ports.json"), Token);
        var preparation = new CurrentSourcePreparation(new("current-source"), new(new(1e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt)));
        var bindings = profiles.Bind(runtime.CreateImportBindings(currentSourcePreparations: [preparation]), new("matrix"),
            [KeyValuePair.Create(new WiringProfileId("k6221_k2182"), preparation.Device)], new("k6221_k2182"), channelVoltageMeter: new("nanovoltmeter"));
        var plan = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(root, "plans", "ppms-iv-meter-import.json"), bindings, Token);
        var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(plan, Token); Assert.Equal(3, runs.LatestDataSet!.Rows.Count);
        Assert.Equal(new[] { -1e-5, 0, 1e-5 }, runs.LatestDataSet.Rows.Select(r => r.Values[new("ppms.root.axis")].Value));
        Assert.All(runs.LatestDataSet.Rows, row => Assert.Equal(new Quantity(0.001, PhysicalUnit.Volt), row.Values[new("ppms.root.0.value")]));
        Assert.True((await runtime.Registry.GetStatusAsync(preparation.Device, Token)).SafetyConfirmed); Assert.True((await runtime.Registry.GetStatusAsync(new("matrix"), Token)).SafetyConfirmed);
        Assert.False(runs.HasActiveRun);
    }
    private static JsonObject IvMeterTree(string channel = "")
    {
        var child = new JsonObject
        {
            ["type"] = "measurement",
            ["name"] = "measure_voltage",
            ["params"] = new JsonObject
            { ["instrument"] = "k2182", ["meter_range_v"] = "0.02", ["nplc"] = 2, ["filter_enabled"] = true, ["filter_count"] = 4 }
        };
        if (channel.Length != 0) child["params"]!["channel"] = channel;
        return new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject
            { ["channel"] = "contacts", ["points"] = new JsonArray(-1e-5, 0, 1e-5), ["settle_s"] = 0, ["compliance_v"] = 100 },
            ["children"] = new JsonArray(child)
        };
    }
}

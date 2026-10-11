using System.Diagnostics;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TemperatureControlExecutionTests
{
    private static TemperatureStabilityStep Settings(Bench b, double seconds) => new(new("settings"), b.Id, new(stableFor: TimeSpan.FromSeconds(seconds)));
    private static WaitTemperatureStep ThermalWait(Bench b, TemperatureWaitMode mode = TemperatureWaitMode.Stable, double timeout = 1)
        => new(new("wait.temperature"), b.Id, new ConstantValue(K(300)), mode, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(timeout));
    private static ExperimentPlan ThermalPlan(Bench b, double seconds, TemperatureWaitMode mode = TemperatureWaitMode.Stable, double timeout = 1)
        => new("Thermal evidence", new SequenceStep(new("root"), [Settings(b, seconds), ThermalWait(b, mode, timeout), b.Read()]));

    [Fact]
    public async Task StabilityPatchIsSessionOwnedAndNeverMutatesHardwareFlagsTargetOrRate()
    {
        var b = new Bench(); var before = await b.Port.Mock.GetSnapshotAsync(Token);
        var changed = await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(K(0.3), stableFor: TimeSpan.Zero), Token);
        Assert.Equal(K(0.3), changed.Tolerance); Assert.Equal(K(2), changed.SettlingThreshold);
        var after = await b.Port.Mock.GetSnapshotAsync(Token);
        Assert.Equal(before.Target, after.Target); Assert.Equal(before.Rate, after.Rate); Assert.False(after.Connected);
        Assert.Equal(0, b.Port.Applies);
        await b.Devices.ConnectAsync(b.Id, Token);
        Assert.Equal(new TemperatureControlConfiguration(true, true), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(K(0.9)), cancelled.Token));
        Assert.Equal(changed, await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token));
        await b.Runs.StartPlanAsync(new("Uses current settings", new SequenceStep(new("root"), [ThermalWait(b), b.Read()])), Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal("0.3", Assert.Single(b.Runs.LatestDataSet.Instruments).Configuration["temperature_tolerance_k"]);
        Assert.Equal("0", Assert.Single(b.Runs.LatestDataSet.Instruments).Configuration["temperature_stable_duration_s"]);
    }

    [Theory]
    [InlineData(TemperatureWaitMode.Reached, true)]
    [InlineData(TemperatureWaitMode.Stable, false)]
    public async Task ReachedUsesCurrentToleranceWhileStableRequiresConfiguredDuration(TemperatureWaitMode mode, bool succeeds)
    {
        var b = new Bench(); var plan = ThermalPlan(b, 0.4, mode, timeout: 0.06);
        if (succeeds) await b.Runs.StartPlanAsync(plan, Token);
        else await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.Equal(succeeds ? 1 : 0, b.Runs.LatestDataSet!.Rows.Count);
        Assert.False(b.Runs.HasActiveRun); Assert.Equal(1, b.Port.Holds);
        Assert.Equal(TimeSpan.FromSeconds(0.4), (await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token)).StableFor);
    }

    [Fact]
    public async Task CoolingDoesNotBecomeReachedAtMatchingReadbackAndTimeoutKeepsControlDisabled()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new ExperimentPlan("Cooling", new SequenceStep(new("root"), [Settings(b, 0), b.Command(TemperatureControlAction.ControlOff), ThermalWait(b, TemperatureWaitMode.Reached, 0.04), b.Read()]));
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        var log = new List<string>(); while (events.Reader.TryRead(out var item)) log.Add(item.Message);
        Assert.Contains(log, message => message.Contains("Temperature wait: Cooling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PauseBlocksManualPatchAndResetsStableEvidenceAfterResume()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var run = b.Runs.StartPlanAsync(ThermalPlan(b, 0.22), Token); await UntilTemperatureState(events, "Reached");
        b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(stableFor: TimeSpan.Zero), Token));
        await Task.Delay(260); Assert.False(run.IsCompleted);
        var resumed = Stopwatch.StartNew(); b.Runs.Resume(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(resumed.Elapsed >= TimeSpan.FromMilliseconds(200)); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task AbortPausedThermalWaitKeepsPartialRowsAndUsesIndependentHold()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new ExperimentPlan("Partial wait", new SequenceStep(new("root"), [b.Read(), Settings(b, 30), ThermalWait(b, timeout: 60)]));
        var run = b.Runs.StartPlanAsync(plan, Token); await UntilTemperatureState(events, "Reached");
        b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(InstrumentControlWorkbench.Core.Measurements.MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.False(b.Runs.HasActiveRun); Assert.Equal(1, b.Port.Holds);
        var held = await b.Port.Mock.GetSnapshotAsync(Token); Assert.Equal(held.Readback, held.Target); Assert.False(held.Moving);
    }

    [Fact]
    public async Task TargetDriftFailsWaitAndFailedHoldRetainsOwnershipUntilRecovery()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var run = b.Runs.StartPlanAsync(ThermalPlan(b, 0.5), Token); await UntilTemperatureState(events, "Reached");
        b.Port.FailHold = true; await b.Port.Mock.SetTargetAsync(K(301), Token);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("target changed", error.ToString()); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token));
        b.Port.FailHold = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("inconsistent")]
    [InlineData("cancelled")]
    public async Task StabilityReadFailuresAndCancellationRemainFatalAndStillHold(string fault)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); using var cancelled = new CancellationTokenSource();
        var run = b.Runs.StartPlanAsync(ThermalPlan(b, 0.5), cancelled.Token); await UntilTemperatureState(events, "Reached");
        if (fault == "cancelled")
        {
            b.Port.BlockNextConfigurationRead = true;
            await b.Port.ConfigurationBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(InstrumentControlWorkbench.Core.Measurements.MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        }
        else
        {
            b.Port.FailNextConfigurationRead = fault == "unreadable"; b.Port.InconsistentNextConfigurationRead = fault == "inconsistent";
            var error = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            if (fault == "unreadable") Assert.Same(b.Port.HoldReadFailure, error);
            else Assert.Contains("flags changed", error.Message);
        }
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun); Assert.Equal(1, b.Port.Holds);
        Assert.Equal(new TemperatureControlConfiguration(true, true), await b.Port.Mock.ReadControlConfigurationAsync(Token));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("wait")]
    public void UnsupportedThermalProfileRejectsWholePlanBeforeIo(string step)
    {
        var hub = new NotificationHub(); var source = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var field = MockEnvironmentCatalog.Field();
        var registry = new DeviceRegistry([source, new AxisControllerSession(field, hub)], leases);
        var runs = new MeasurementRunService(source, leases, hub, deviceRegistry: registry);
        ExperimentStep unsupported = step == "config" ? new TemperatureStabilityStep(new("config"), field.Definition.Device.Id, new())
            : new WaitTemperatureStep(new("wait"), field.Definition.Device.Id, new ConstantValue(K(300)), TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        var conditional = new IfElseStep(new("if"), new(new("known"), ComparisonOperator.NotEqual, new ConstantValue(K(300))), unsupported);
        var plan = new ExperimentPlan("Invalid unselected branch", new SequenceStep(new("root"), [new AssignValueStep(new("assign"), new("known"), new ConstantValue(K(300))), conditional]));
        var error = Assert.ThrowsAny<Exception>(() => runs.PreviewPlan(plan)); Assert.Contains("profile", error.Message);
    }

    [Theory]
    [InlineData("{\"tolerance\":0.2,\"settling_threshold\":3,\"stable_duration\":0}")]
    [InlineData("{\"tolerance_k\":0.2,\"settling_threshold_k\":3,\"stable_duration_s\":0}")]
    [InlineData("{}")]
    public async Task PpmsSettingsAliasesAndEmptyPatchRoundTripAndRemainIndependent(string parameters)
    {
        var b = new Bench(); var imported = new PpmsExperimentPlanImporter().Parse(Node("temp_set_config", JsonNode.Parse(parameters)!.AsObject()).ToJsonString(), b.Bindings);
        var codec = new JsonExperimentPlanDocument(); await b.Runs.StartPlanAsync(codec.Parse(codec.Serialize(imported)), Token);
        Assert.Equal(0, b.Port.Applies); Assert.Equal(new TemperatureControlConfiguration(true, true), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        var actual = await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token);
        Assert.Equal(parameters == "{}" ? TemperatureStabilitySettings.Default : new(K(0.2), K(3), TimeSpan.Zero), actual);
    }

    [Theory]
    [InlineData("{\"tolerance_k\":-1}")]
    [InlineData("{\"settling_threshold_k\":-1}")]
    [InlineData("{\"stable_duration_s\":-1}")]
    [InlineData("{\"stable_duration_s\":999999999}")]
    [InlineData("{\"tolerance_k\":null}")]
    [InlineData("{\"tolerance_k\":\"0.2\"}")]
    [InlineData("{\"tolerance_k\":0.2,\"tolerance\":0.2}")]
    [InlineData("{\"stable_duration_s\":0,\"stable_duration\":0}")]
    [InlineData("{\"settling_threshold_k\":2,\"settling_threshold\":2}")]
    [InlineData("{\"enabled\":true}")]
    public void InvalidStabilityImportReportsSourceLineBeforeHardware(string parameters)
    {
        var b = new Bench();
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(Node("temp_set_config", JsonNode.Parse(parameters)!.AsObject()).ToJsonString(), b.Bindings));
        Assert.Contains("17", error.Message); Assert.Equal(0, b.Port.Applies); Assert.Equal(0, b.Port.Holds);
    }

    [Theory]
    [InlineData("config_extra")]
    [InlineData("config_null")]
    [InlineData("config_unit")]
    [InlineData("config_negative")]
    [InlineData("wait_unknown")]
    [InlineData("wait_numeric")]
    [InlineData("wait_zero")]
    public void StrictNativeStabilityDocumentsRejectMalformedFields(string fault)
    {
        var b = new Bench(); var codec = new JsonExperimentPlanDocument();
        var native = JsonNode.Parse(codec.Serialize(ThermalPlan(b, 0)))!.AsObject(); var steps = native["root"]!["children"]!.AsArray();
        var settings = steps[0]!.AsObject(); var wait = steps[1]!.AsObject();
        switch (fault)
        {
            case "config_extra": settings["enabled"] = true; break;
            case "config_null": settings["stableSeconds"] = null; break;
            case "config_unit": settings["tolerance"] = new JsonObject { ["value"] = 0.2, ["unit"] = "Tesla" }; break;
            case "config_negative": settings["stableSeconds"] = -1; break;
            case "wait_unknown": wait["mode"] = "Cooling"; break;
            case "wait_numeric": wait["mode"] = 0; break;
            case "wait_zero": wait["pollSeconds"] = 0; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(native.ToJsonString()));
    }

    [Fact]
    public async Task InheritedSettingsFixtureExecutesQuantizedNestedScanArchivesAndReplaysFresh()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"temperature-stability-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-temperature-stability-import.json"));
                var imported = new PpmsExperimentPlanImporter().Parse(text, b.Bindings); var codec = new JsonExperimentPlanDocument();
                var nativePath = Path.Combine(folder, "converted.plan.json"); await codec.SaveAsync(nativePath, imported, Token);
                await b.Runs.StartPlanAsync(await codec.LoadAsync(nativePath, Token), Token); id = b.Runs.LatestDataSet!.Id;
                Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count); Assert.Equal(0, b.Port.Applies);
                Assert.Equal(TimeSpan.Zero, (await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token)).StableFor);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(id, Token);
            Assert.Equal(2, stored.DataSet.Rows.Count); Assert.Equal(3, stored.DataSet.Plan!.Metadata["ppms.root.1.0.source_line"].Value);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan, Token); Assert.Equal(2, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "temperature.csv"); await reopened.ExportCsvAsync(id, csv, Token);
            Assert.Contains("temperature_stability", new JsonExperimentPlanDocument().Serialize(stored.DataSet.Plan)); Assert.Contains("Kelvin", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("set")]
    [InlineData("scan")]
    public async Task ScanOverridesPersistButWaitNoneSetSkipsStabilityChanges(string kind)
    {
        var b = new Bench(); var node = kind == "set" ? new JsonObject { ["type"] = "command", ["name"] = "set_temp", ["params"] = new JsonObject { ["target_k"] = 300, ["wait_mode"] = "none", ["tolerance_k"] = 0.4, ["stable_duration_s"] = 0 } }
            : new JsonObject { ["type"] = "scan", ["name"] = "scan_temp", ["params"] = new JsonObject { ["points"] = new JsonArray(300), ["tolerance_k"] = 0.4, ["stable_duration_s"] = 0 }, ["children"] = new JsonArray(new JsonObject { ["type"] = "measurement", ["name"] = "measure_temp" }) };
        var plan = new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(plan, Token);
        var actual = await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token);
        Assert.Equal(kind == "set" ? TemperatureStabilitySettings.Default.Tolerance : K(0.4), actual.Tolerance);
        Assert.Equal(kind == "set" ? TemperatureStabilitySettings.Default.StableFor : TimeSpan.Zero, actual.StableFor); Assert.Equal(K(2), actual.SettlingThreshold);
    }

    private static async Task UntilTemperatureState(NotificationSubscription events, string state)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!(await events.Reader.ReadAsync(deadline.Token)).Message.Contains($"Temperature wait: {state}", StringComparison.Ordinal)) { }
    }
}

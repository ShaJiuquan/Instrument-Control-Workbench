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
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TemperatureControlExecutionTests
{
    private static ExperimentStep Setpoint(Bench b, bool hardware, bool enable = false, bool direct = false)
        => hardware ? new HardwareTemperatureSetpointStep(new("setpoint"), b.Id, new ConstantValue(K(300.1)), Rate(7), enable)
            : new SoftwareTemperatureRampStep(new("setpoint"), b.Id, new ConstantValue(K(300.1)), direct ? null : Rate(7), enable);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task PreserveControlSetpointsNeverEnableOrDisableObservedControl(bool hardware, bool originalControl, bool direct)
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token); await b.Port.Mock.ConfigureControlAsync(new(true, originalControl), Rate(5), Token);
        var plan = new ExperimentPlan("Preserved control", new SequenceStep(new("root"), [Setpoint(b, hardware, direct: direct), b.Read()]));
        var codec = new JsonExperimentPlanDocument(); var text = codec.Serialize(plan); Assert.Contains("\"enableControl\": false", text);
        await b.Runs.StartPlanAsync(codec.Parse(text), Token);
        Assert.Equal(new TemperatureControlConfiguration(hardware, originalControl), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        Assert.Equal(hardware ? Rate(7) : Rate(5), (await b.Port.Mock.GetSnapshotAsync(Token)).Rate);
        Assert.Equal(1, b.Port.Applies); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        if (!originalControl) Assert.Equal(K(300), Assert.Single(b.Runs.LatestDataSet.Rows).Values[new("temperature.actual")]);
    }

    [Fact]
    public async Task ExplicitHardwareEnableParksLiveOriginBeforeRateEnablesControl()
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token); await b.Port.Mock.ConfigureControlAsync(new(true, false), null, Token);
        await b.Port.Mock.SetTargetAsync(K(305), Token);
        await b.Runs.StartPlanAsync(new("Enable hardware", Setpoint(b, hardware: true, enable: true)), Token);
        Assert.Equal(K(300), Assert.Single(b.Port.HardwareRampTargets));
        Assert.Equal(new TemperatureControlConfiguration(true, true), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        Assert.Equal(Rate(7), (await b.Port.Mock.GetSnapshotAsync(Token)).Rate);
    }

    [Theory]
    [InlineData(false, "ignored")]
    [InlineData(false, "target")]
    [InlineData(false, "rate")]
    [InlineData(false, "throw")]
    [InlineData(true, "target")]
    [InlineData(true, "rate")]
    [InlineData(true, "throw")]
    public async Task PreservedSetpointRefusalOrDriftFailsAndUsesIndependentHold(bool hardware, string fault)
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token); await b.Port.Mock.ConfigureControlAsync(new(!hardware, false), null, Token); b.Port.Fault = fault;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(new("Refused setpoint", Setpoint(b, hardware)), Token));
        if (fault == "throw") Assert.Same(b.Port.Failure, error);
        else Assert.Contains("did not", error.Message);
        Assert.False(b.Runs.HasActiveRun); Assert.Equal(1, b.Port.Holds); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringPreservedConfigurationDoesNotStartControl(bool hardware)
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token); await b.Port.Mock.ConfigureControlAsync(new(true, false), null, Token);
        b.Port.Block = true; using var cancelled = new CancellationTokenSource();
        var run = b.Runs.StartPlanAsync(new("Cancelled setpoint", Setpoint(b, hardware)), cancelled.Token);
        await b.Port.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
        Assert.Equal(1, b.Port.Holds); Assert.False(b.Runs.HasActiveRun);
        Assert.Equal(InstrumentControlWorkbench.Core.Measurements.MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
    }

    [Fact]
    public async Task PausedPreservedSoftwareRampRestoresOnlyConfirmedOriginalFlagsAndRemainingTick()
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token); await b.Port.Mock.ConfigureControlAsync(new(true, false), null, Token); using var events = b.Hub.Subscribe();
        var step = new SoftwareTemperatureRampStep(new("ramp"), b.Id, new ConstantValue(K(300.15)), Rate(5), enableControl: false);
        var run = b.Runs.StartPlanAsync(new("Paused disabled ramp", new SequenceStep(new("root"), [step, b.Read()])), Token);
        await b.Port.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.ControlOn, null, Token));
        b.Runs.Resume(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task PreservedSetpointFailureRetainsWholeResourceGroupUntilExplicitRecovery()
    {
        var b = new Bench(); b.Port.FailHold = true;
        var sourceRead = new ReadStep(new("source.read"), [new(new("source.current"), new(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(new("Group", new SequenceStep(new("root"), [sourceRead, Setpoint(b, false)])), Token));
        Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConnectAsync(b.Source.Id, Token));
        b.Port.FailHold = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Theory]
    [InlineData("software", 7)]
    [InlineData("hardware", 7)]
    [InlineData("hardware", 0)]
    [InlineData("auto", 7)]
    [InlineData("auto", 0)]
    public async Task PpmsSetFalsePreservesControlAndWaitNoneSkipsThresholdsAndSettle(string strategy, double rate)
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token); await b.Port.Mock.ConfigureControlAsync(new(true, false), null, Token);
        var node = new JsonObject
        {
            ["type"] = "command",
            ["name"] = "set_temp",
            ["source_line"] = 9,
            ["params"] = new JsonObject { ["target_k"] = 300.05, ["rate_k_per_min"] = rate, ["enable_control"] = false, ["ramp_strategy"] = strategy, ["wait_mode"] = "none", ["settle_s"] = 60, ["tolerance_k"] = 0.8, ["stable_duration_s"] = 0 }
        };
        var plan = new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), b.Bindings);
        var text = new JsonExperimentPlanDocument().Serialize(plan); Assert.DoesNotContain("temperature_stability", text); Assert.DoesNotContain("\"kind\": \"delay\"", text);
        await b.Runs.StartPlanAsync(plan, Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
        Assert.Equal(strategy == "hardware" && rate > 0, (await b.Port.Mock.ReadControlConfigurationAsync(Token)).HardwareRampEnabled);
        Assert.Equal(TemperatureStabilitySettings.Default, await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSetpointBooleanIsStrictAndOmissionRetainsLegacyEnableSemantics(bool hardware)
    {
        var b = new Bench(); var codec = new JsonExperimentPlanDocument(); var encoded = codec.Serialize(new("Legacy", Setpoint(b, hardware, enable: true)));
        Assert.DoesNotContain("enableControl", encoded);
        var native = JsonNode.Parse(encoded)!.AsObject(); native["root"]!["enableControl"] = "false";
        Assert.ThrowsAny<Exception>(() => codec.Parse(native.ToJsonString()));
        native["root"]!["enableControl"] = null; Assert.ThrowsAny<Exception>(() => codec.Parse(native.ToJsonString()));
        native["root"]!["enableControl"] = false; var parsed = codec.Parse(native.ToJsonString()).Root;
        Assert.False(parsed is HardwareTemperatureSetpointStep h ? h.EnableControl : ((SoftwareTemperatureRampStep)parsed).EnableControl);
    }

    [Fact]
    public async Task PreservedSetpointFixtureArchivesAndReplaysFreshWithoutHeating()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"preserved-setpoint-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-preserved-temperature-setpoints-import.json"));
                var plan = new PpmsExperimentPlanImporter().Parse(text, b.Bindings); var codec = new JsonExperimentPlanDocument();
                await codec.SaveAsync(Path.Combine(folder, "converted.json"), plan, Token); await b.Runs.StartPlanAsync(await codec.LoadAsync(Path.Combine(folder, "converted.json"), Token), Token);
                id = b.Runs.LatestDataSet!.Id; Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count);
                Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
            }
            await using var archive2 = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await archive2.LoadAsync(id, Token);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Equal(2, fresh.Runs.LatestDataSet!.Rows.Count);
            Assert.False((await fresh.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
            await archive2.ExportCsvAsync(id, Path.Combine(folder, "rows.csv"), Token); Assert.Contains("Kelvin", await File.ReadAllTextAsync(Path.Combine(folder, "rows.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservedModeRequiresActualOptionalPortBeforeAnyDeviceConnect(bool hardware)
    {
        var b = new Bench(); var hub = new NotificationHub(); var source = new InstrumentManager(b.Source, hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([source, new AxisControllerSession(new RampOnlyTemperaturePort(b.Port), hub)], leases);
        var runs = new MeasurementRunService(source, leases, hub, deviceRegistry: registry);
        Assert.Throws<NotSupportedException>(() => runs.PreviewPlan(new("Unsupported preservation", Setpoint(b, hardware))));
        Assert.False((await b.Port.Mock.GetSnapshotAsync(Token)).Connected); Assert.Equal(0, b.Port.Applies); Assert.Equal(0, b.Port.Holds);
        await runs.StartPlanAsync(new("Legacy enabled capability", Setpoint(b, hardware, enable: true)), Token);
        Assert.Equal(new TemperatureControlConfiguration(hardware, true), await b.Port.Mock.ReadControlConfigurationAsync(Token));
    }

    [Theory]
    [InlineData("enable_control", "string")]
    [InlineData("enable_control", "null")]
    [InlineData("tolerance_k", "string")]
    [InlineData("stable_duration_s", "negative")]
    public void MalformedUnusedSetFieldsStillRejectAtImportBoundary(string field, string value)
    {
        var b = new Bench(); var parameters = new JsonObject { ["target_k"] = 300, ["wait_mode"] = "none" };
        parameters[field] = value == "null" ? null : value == "negative" ? JsonValue.Create(-1) : JsonValue.Create("false");
        var node = new JsonObject { ["type"] = "command", ["name"] = "set_temp", ["source_line"] = 11, ["params"] = parameters };
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), b.Bindings));
        Assert.Contains("11", error.Message); Assert.Equal(0, b.Port.Applies);
    }

    [Fact]
    public async Task FailedRampDoesNotApplyWaitThresholdsOrStartStabilityWait()
    {
        var b = new Bench(); b.Port.Fault = "target"; using var events = b.Hub.Subscribe();
        var node = new JsonObject
        {
            ["type"] = "command",
            ["name"] = "set_temp",
            ["params"] = new JsonObject
            { ["target_k"] = 300.05, ["enable_control"] = false, ["wait_mode"] = "stable", ["tolerance_k"] = 0.7, ["stable_duration_s"] = 0 }
        };
        var plan = new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), b.Bindings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.Equal(TemperatureStabilitySettings.Default, await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token));
        Assert.Equal(1, b.Port.Holds); Assert.False(b.Runs.HasActiveRun);
        var started = new List<string>(); while (events.Reader.TryRead(out var item)) if (item.Kind == WorkbenchNotificationKind.StepStarted) started.Add(item.Step!.Value);
        Assert.DoesNotContain(started, id => id.EndsWith("stability_config", StringComparison.Ordinal) || id.EndsWith("stable", StringComparison.Ordinal));
    }

    private sealed class RampOnlyTemperaturePort(TemperatureProbe port) : ITemperatureControlPort
    {
        public AxisControllerDefinition Definition => port.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => port.ConnectAsync(token);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => port.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity target, CancellationToken token) => port.SetTargetAsync(target, token);
        public Task SetRateAsync(Quantity rate, CancellationToken token) => port.SetRateAsync(rate, token);
        public Task PrepareSoftwareRampAsync(Quantity target, CancellationToken token) => port.PrepareSoftwareRampAsync(target, token);
        public Task SetHardwareRampAsync(Quantity rate, CancellationToken token) => port.SetHardwareRampAsync(rate, token);
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token) => port.ReadControlConfigurationAsync(token);
        public Task HoldAsync(CancellationToken token) => port.HoldAsync(token);
        public Task DisconnectAsync(CancellationToken token) => port.DisconnectAsync(token);
    }

}

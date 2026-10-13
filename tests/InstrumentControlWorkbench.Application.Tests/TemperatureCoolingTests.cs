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

public sealed class TemperatureCoolingTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private sealed class Port : ITemperatureConfigurationPort
    {
        public MockAxisController Inner { get; } = MockEnvironmentCatalog.PassiveTemperature();
        public AxisControllerDefinition Definition => Inner.Definition;
        public bool RefuseOff { get; set; }
        public bool RefuseRestore { get; set; }
        public bool RefuseHold { get; set; }
        public List<TemperatureControlConfiguration> Configurations { get; } = [];
        public TaskCompletionSource Begun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken t) => Inner.ConnectAsync(t);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken t) => Inner.GetSnapshotAsync(t);
        public Task SetTargetAsync(Quantity target, CancellationToken t) { Begun.TrySetResult(); return Inner.SetTargetAsync(target, t); }
        public Task SetRateAsync(Quantity rate, CancellationToken t) => Inner.SetRateAsync(rate, t);
        public Task HoldAsync(CancellationToken t) => RefuseHold ? Task.FromException(new IOException("hold refused")) : Inner.HoldAsync(t);
        public Task DisconnectAsync(CancellationToken t) => Inner.DisconnectAsync(t);
        public Task PrepareSoftwareRampAsync(Quantity target, CancellationToken t) => Inner.PrepareSoftwareRampAsync(target, t);
        public Task SetHardwareRampAsync(Quantity rate, CancellationToken t) => Inner.SetHardwareRampAsync(rate, t);
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken t) => Inner.ReadControlConfigurationAsync(t);
        public Task ConfigureControlAsync(TemperatureControlConfiguration c, Quantity? rate, CancellationToken t)
        {
            Configurations.Add(c); if (!c.ControlEnabled && RefuseOff || c.ControlEnabled && RefuseRestore) throw new IOException("control change refused"); return Inner.ConfigureControlAsync(c, rate, t);
        }
    }
    private sealed class Bench
    {
        public Port Port { get; } = new(); public NotificationHub Hub { get; } = new(); public DeviceRegistry Devices { get; }
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public MeasurementRunService Runs { get; }
        public MockKeithley2450SourceMeter? Source { get; }
        public Bench(IRunResultListener? listener = null, bool withSource = false)
        {
            var sessions = new List<IDeviceSession> { new AxisControllerSession(Port, Hub) };
            if (withSource) { Source = new(); sessions.Add(new InstrumentManager(Source, Hub)); }
            Devices = new(sessions, Leases); Runs = new(Devices, Hub, listener is null ? null : [listener]);
        }
        public ReadStep Read => new(new("read"), [new(new("temperature"), new(Port.Definition.Device.Id, Port.Definition.Readback.Id))]);
        public TemperatureCoolingStep Cooling(CoolingSamplingMode mode = CoolingSamplingMode.Threshold, ExperimentStep? body = null, double end = 3.8, double tolerance = .001, TimeSpan? timeout = null, string id = "cool") => new(new(id), Port.Definition.Device.Id, new("axis"), new([K(4), K(3.9), K(end)]), K(end), K(end), K(tolerance), timeout ?? TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), mode, TimeSpan.FromMilliseconds(20), body ?? new SequenceStep(new("body"), [Read, new DelayStep(new("delay"), TimeSpan.FromMilliseconds(10))]));
        public ExperimentPlan Plan(ExperimentStep root, long budget = 10000) => new("Cooling", root, Source is null ? [] : [new SourcePreparation(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))], maximumStepExecutions: budget);
    }
    private sealed class Listener(Func<MeasurementDataRow, Task> callback) : IRunResultListener
    {
        public Task OnStartedAsync(RunDataSetSnapshot snapshot, CancellationToken token) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) => callback(row);
        public Task OnFinalizedAsync(RunDataSetSnapshot snapshot, CancellationToken token) => Task.CompletedTask;
    }
    [Theory]
    [InlineData(CoolingSamplingMode.Threshold)]
    [InlineData(CoolingSamplingMode.Timed)]
    [InlineData(CoolingSamplingMode.Immediate)]
    public async Task EverySamplingModeObservesActualCoolingAndRestoresControlOnlyOnSuccess(CoolingSamplingMode mode)
    {
        var b = new Bench(); var step = b.Cooling(mode); await b.Runs.StartPlanAsync(b.Plan(step), Token); var data = b.Runs.LatestDataSet!; Assert.NotEmpty(data.Rows);
        var values = data.Rows.Select(r => r.Values[step.Column].Value).ToArray(); Assert.True(values[^1] <= step.EndTemperature.Value + step.Tolerance.Value); Assert.All(values, v => Assert.InRange(v, 1.7, 4));
        Assert.All(data.Rows, r => Assert.Equal(mode == CoolingSamplingMode.Threshold ? 3 : 0, r.Values[step.TotalColumn].Value)); Assert.Equal(Enumerable.Range(0, data.Rows.Count).Select(i => (double)i), data.Rows.Select(r => r.Values[step.IndexColumn].Value));
        Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.Equal(new(false, false), b.Port.Configurations[0]); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ThresholdScanStopsEarlyWhenLiveTemperatureAlreadyCrossedEnd()
    {
        var b = new Bench(); await b.Devices.ConnectAsync(b.Port.Definition.Device.Id, Token); await b.Port.Inner.ConfigureControlAsync(new(false, false), null, Token); await Task.Delay(140);
        var step = b.Cooling(); await b.Runs.StartPlanAsync(b.Plan(step), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(row.Values[step.Column].Value < 3.8); Assert.Equal(0, row.Values[step.IndexColumn].Value);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortOrParentDeadlineKeepsRampAndControlOff(bool deadline)
    {
        var b = new Bench(); var step = b.Cooling(body: new DelayStep(new("long"), TimeSpan.FromSeconds(30))); ExperimentStep root = deadline ? new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(200), step) : step;
        var running = b.Runs.StartPlanAsync(b.Plan(root), Token); await b.Port.Begun.Task.WaitAsync(TimeSpan.FromSeconds(5)); if (!deadline) b.Runs.Abort();
        if (deadline) await Assert.ThrowsAsync<PlanStepTimeoutException>(() => running); else await running;
        Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token)); Assert.DoesNotContain(b.Port.Configurations, c => c.ControlEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task PauseAllowsNaturalCoolingButRetainsLeaseAndValidatesResume()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var step = b.Cooling(body: new SequenceStep(new("body"), [b.Read, new DelayStep(new("delay"), TimeSpan.FromMilliseconds(250))])); var running = b.Runs.StartPlanAsync(b.Plan(step), Token);
        await b.Port.Begun.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        var before = await b.Port.GetSnapshotAsync(Token); await Task.Delay(80); var after = await b.Port.GetSnapshotAsync(Token); Assert.True(after.Readback.Value < before.Readback.Value); Assert.Equal(before.Target, after.Target); Assert.False(after.Moving); Assert.True(b.Leases.IsLeased(b.Port.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1)));
        b.Runs.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ListenerFailurePreservesCommittedRowButNeverRestoresControl()
    {
        var failure = new IOException("result listener failed"); var b = new Bench(new Listener(_ => Task.FromException(failure)));
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(b.Cooling()), Token)); Assert.Same(failure, error); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FailedOffObligationRetainsOwnershipEvenWhenOrdinaryHoldCanSucceed()
    {
        Bench? b = null; b = new(new Listener(async _ => { await b!.Port.Inner.ConfigureControlAsync(new(false, true), null, Token); b.Port.RefuseOff = true; }), withSource: true);
        var body = new SequenceStep(new("body"), [new SourceOutputStep(new("on"), b.Source!.Id, true), new ReadSourceSenseStep(new("source-read"), b.Source.Id, SourceFunction.Voltage, new("v"), new("i"))]);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Cooling(body: body)), Token)); Assert.True(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1))); Assert.True(b.Leases.IsLeased(b.Port.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1))); b.Port.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token));
    }
    [Fact]
    public async Task FailedInitialOffNeverProgramsTargetAndRequiresExplicitRecovery()
    {
        var b = new Bench(); b.Port.RefuseOff = true; await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Cooling()), Token)); Assert.False(b.Port.Begun.Task.IsCompleted); Assert.True(b.Runs.HasActiveRun); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        b.Port.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token));
    }
    [Fact]
    public async Task PausedControlDriftRejectsResumeAndCleanupDisablesItAgain()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Cooling(body: new DelayStep(new("body"), TimeSpan.FromSeconds(1)))), Token); await b.Port.Begun.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        await b.Port.Inner.ConfigureControlAsync(new(false, true), null, Token); b.Runs.Resume(); await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task TimedIntervalKeepsWallClockProgressWhilePaused()
    {
        Bench? b = null; var rows = 0; var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b = new(new Listener(_ => { if (++rows == 1) b!.Runs.Pause(); else second.TrySetResult(); return Task.CompletedTask; })); using var events = b.Hub.Subscribe();
        var original = b.Cooling(CoolingSamplingMode.Timed, body: b.Read); var step = new TemperatureCoolingStep(original.Id, original.Device, original.Column, original.Thresholds, original.CoolingTarget, original.EndTemperature, original.Tolerance, original.Timeout, original.PollInterval, original.Sampling, TimeSpan.FromSeconds(1), original.Body);
        var running = b.Runs.StartPlanAsync(b.Plan(step), Token); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        await Task.Delay(1100); var resumed = System.Diagnostics.Stopwatch.StartNew(); b.Runs.Resume(); await second.Task.WaitAsync(TimeSpan.FromMilliseconds(500)); Assert.True(resumed.Elapsed < TimeSpan.FromMilliseconds(500)); await running; Assert.Equal(2, rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FailedNormalRestoreFallsBackToVerifiedOffAndFaults()
    {
        var b = new Bench(); b.Port.RefuseRestore = true; await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(b.Cooling()), Token)); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task LogicalChildFailureForcedSuccessfulOutsideScanStillKeepsControlOff()
    {
        var b = new Bench(); var body = new ResultDecoratorStep(new("failure"), ResultEffect.ForceFailure, b.Read); var scan = b.Cooling(body: body); var root = new ResultDecoratorStep(new("forced"), ResultEffect.ForceSuccess, scan); await b.Runs.StartPlanAsync(b.Plan(root), Token); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token)); Assert.DoesNotContain(b.Port.Configurations, c => c.ControlEnabled);
    }
    [Fact]
    public async Task ContinuousSamplingStillHonorsGlobalExecutionBudget()
    {
        var b = new Bench(); var scan = b.Cooling(CoolingSamplingMode.Immediate, end: 1.7); await Assert.ThrowsAsync<PlanExecutionBudgetException>(() => b.Runs.StartPlanAsync(b.Plan(scan, 8), Token)); Assert.Equal(new TemperatureControlConfiguration(false, false), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("target")]
    [InlineData("rate")]
    [InlineData("control")]
    [InlineData("nested")]
    public async Task CoolingChildrenCannotReconfigureTheirOwnedController(string kind)
    {
        var b = new Bench(); var id = b.Port.Definition.Device.Id;
        ExperimentStep body = kind switch { "target" => new SetParameterStep(new("write"), new(id, b.Port.Definition.Target.Id), new ConstantValue(K(3))), "rate" => new SetParameterStep(new("write"), new(id, b.Port.Definition.Rate.Id), new ConstantValue(new(1, PhysicalUnit.KelvinPerMinute))), "control" => new TemperatureControlStep(new("write"), id, TemperatureControlAction.ControlOn), _ => b.Cooling(id: "nested") };
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Plan(b.Cooling(body: body)))); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task ThresholdDeadlineWithStationaryTemperatureFailsInsteadOfInventingCrossing()
    {
        var ordinary = MockEnvironmentCatalog.Temperature(); var hub = new NotificationHub(); var registry = new DeviceRegistry([new AxisControllerSession(ordinary, hub)], new(TimeSpan.FromSeconds(5))); var runs = new MeasurementRunService(registry, hub);
        var scan = new TemperatureCoolingStep(new("cool"), ordinary.Definition.Device.Id, new("axis"), new([K(4), K(3)]), K(3), K(3), K(.01), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), CoolingSamplingMode.Threshold, TimeSpan.FromMilliseconds(20), new DelayStep(new("body"), TimeSpan.Zero));
        await Assert.ThrowsAsync<TimeoutException>(() => runs.StartPlanAsync(new("Timeout", scan), Token)); Assert.Empty(runs.LatestDataSet!.Rows); Assert.Equal(new TemperatureControlConfiguration(false, false), await ordinary.ReadControlConfigurationAsync(Token)); Assert.False(runs.HasActiveRun);
    }
}

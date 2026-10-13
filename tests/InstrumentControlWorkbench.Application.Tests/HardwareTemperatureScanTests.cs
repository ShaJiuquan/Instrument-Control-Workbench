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
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class HardwareTemperatureScanTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private sealed class Port : ITemperatureConfigurationPort
    {
        public MockAxisController Inner { get; } = MockEnvironmentCatalog.PassiveTemperature();
        public AxisControllerDefinition Definition => Inner.Definition;
        public bool RefuseRampOff { get; set; }
        public bool Freeze { get; set; }
        public bool OscillateFirst { get; set; }
        public int FirstObservations { get; private set; }
        public int ObservationsAtEnable { get; private set; }
        private bool firstProgrammed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TemperatureControlConfiguration> Configurations { get; } = [];
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken t) => Inner.ConnectAsync(t);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken t)
        {
            var s = await Inner.GetSnapshotAsync(t); if (!s.Connected) return s; var c = await Inner.ReadControlConfigurationAsync(t);
            if (firstProgrammed && !c.HardwareRampEnabled) FirstObservations++;
            return Freeze ? s with { Readback = K(4) } : OscillateFirst && firstProgrammed && !c.HardwareRampEnabled && FirstObservations == 3 ? s with { Readback = K(4.02) } : s;
        }
        public async Task SetTargetAsync(Quantity q, CancellationToken t) { await Inner.SetTargetAsync(q, t); if (!(await Inner.ReadControlConfigurationAsync(t)).HardwareRampEnabled) firstProgrammed = true; else Started.TrySetResult(); }
        public Task SetRateAsync(Quantity q, CancellationToken t) => Inner.SetRateAsync(q, t);
        public Task HoldAsync(CancellationToken t) { Freeze = false; return Inner.HoldAsync(t); }
        public Task DisconnectAsync(CancellationToken t) => Inner.DisconnectAsync(t);
        public Task PrepareSoftwareRampAsync(Quantity q, CancellationToken t) => Inner.PrepareSoftwareRampAsync(q, t);
        public Task SetHardwareRampAsync(Quantity q, CancellationToken t) => Inner.SetHardwareRampAsync(q, t);
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken t) => Inner.ReadControlConfigurationAsync(t);
        public Task ConfigureControlAsync(TemperatureControlConfiguration c, Quantity? rate, CancellationToken t)
        {
            Configurations.Add(c); if (!c.HardwareRampEnabled && RefuseRampOff) throw new IOException("ramp OFF refused"); if (c.HardwareRampEnabled) ObservationsAtEnable = FirstObservations; return Inner.ConfigureControlAsync(c, rate, t);
        }
    }
    private sealed class Bench
    {
        public Port Port { get; } = new(); public NotificationHub Hub { get; } = new(); public DeviceRegistry Devices { get; }
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public MeasurementRunService Runs { get; }
        public MockKeithley2450SourceMeter? Source { get; }
        public Bench(IRunResultListener? listener = null, bool withSource = false)
        {
            var sessions = new List<IDeviceSession> { new AxisControllerSession(Port, Hub) }; if (withSource) { Source = new(); sessions.Add(new InstrumentManager(Source, Hub)); }
            Devices = new(sessions, Leases); Runs = new(Devices, Hub, listener is null ? null : [listener]);
        }
        public ReadStep Read => new(new("read"), [new(new("temperature"), new(Port.Definition.Device.Id, Port.Definition.Readback.Id))]);
        public HardwareTemperatureScanStep Scan(TemperatureRampSampling sampling = TemperatureRampSampling.Threshold, bool descending = false, bool stable = false, ExperimentStep? body = null, TimeSpan? timeout = null) => new(new("scan"), Port.Definition.Device.Id, new("axis"), new([K(4), K(descending ? 3.99 : 4.01), K(descending ? 3.98 : 4.02)]), new(20, PhysicalUnit.KelvinPerMinute), stable, K(.0005), 3, timeout ?? TimeSpan.FromSeconds(2), timeout ?? TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50), sampling, TimeSpan.FromMilliseconds(20), body ?? new SequenceStep(new("body"), [Read, new DelayStep(new("delay"), TimeSpan.FromMilliseconds(10))]));
        public ExperimentPlan Plan(ExperimentStep root, long budget = 10000) => new("Ramp", root, Source is null ? [] : [new SourcePreparation(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))], maximumStepExecutions: budget);
    }
    private sealed class Listener(Func<MeasurementDataRow, Task> callback) : IRunResultListener
    {
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken t) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken t) => callback(row);
        public Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken t) => Task.CompletedTask;
    }
    [Theory]
    [InlineData(TemperatureRampSampling.Threshold, false)]
    [InlineData(TemperatureRampSampling.Threshold, true)]
    [InlineData(TemperatureRampSampling.EstimatedSetpoint, false)]
    [InlineData(TemperatureRampSampling.EstimatedSetpoint, true)]
    [InlineData(TemperatureRampSampling.Timed, false)]
    [InlineData(TemperatureRampSampling.Timed, true)]
    [InlineData(TemperatureRampSampling.Immediate, false)]
    [InlineData(TemperatureRampSampling.Immediate, true)]
    public async Task FourSamplingModesAcquireActualValuesInBothDirectionsAndConfirmRampOff(TemperatureRampSampling mode, bool descending)
    {
        var b = new Bench(); var step = b.Scan(mode, descending); await b.Runs.StartPlanAsync(b.Plan(step), Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(step.ProgressTotal, row.Values[step.TotalColumn].Value)); Assert.Equal(Enumerable.Range(0, rows.Count).Select(i => (double)i), rows.Select(row => row.Values[step.IndexColumn].Value));
        if (mode is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint) Assert.Equal(3, rows.Count);
        if (mode != TemperatureRampSampling.EstimatedSetpoint) Assert.True(descending ? rows[^1].Values[step.Column].Value <= 3.9805 : rows[^1].Values[step.Column].Value >= 4.0195);
        Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FirstPointRequiresConsecutiveFreshObservationsAndResetsOnExcursion()
    {
        var b = new Bench(); b.Port.OscillateFirst = true; await b.Runs.StartPlanAsync(b.Plan(b.Scan(stable: true)), Token); Assert.True(b.Port.ObservationsAtEnable >= 7); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task EstimatedSetpointSamplingNeverSubstitutesItsEstimateForActualTemperature()
    {
        var b = new Bench(); b.Port.Freeze = true; var step = b.Scan(TemperatureRampSampling.EstimatedSetpoint); await b.Runs.StartPlanAsync(b.Plan(step), Token); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Equal(K(4), row.Values[step.Column])); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(TemperatureRampSampling.Threshold)]
    [InlineData(TemperatureRampSampling.Timed)]
    [InlineData(TemperatureRampSampling.Immediate)]
    public async Task StationarySensorTimesOutRatherThanReportingEndpointSuccess(TemperatureRampSampling mode)
    {
        var b = new Bench(); b.Port.Freeze = true; await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan(mode, timeout: TimeSpan.FromMilliseconds(100))), Token)); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task AbortWhilePausedCannotReuseHoldAsTerminalRampOffProof()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Scan(body: new DelayStep(new("body"), TimeSpan.FromSeconds(30)))), Token); await b.Port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        Assert.Equal(new TemperatureControlConfiguration(true, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False((await b.Port.GetSnapshotAsync(Token)).Moving); b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task PauseAndResumePreserveModeAndResumeOriginalEndTarget()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var step = b.Scan(body: new SequenceStep(new("body"), [b.Read, new DelayStep(new("delay"), TimeSpan.FromMilliseconds(120))])); var running = b.Runs.StartPlanAsync(b.Plan(step), Token); await b.Port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        var held = await b.Port.GetSnapshotAsync(Token); await Task.Delay(80); Assert.Equal(held.Readback, (await b.Port.GetSnapshotAsync(Token)).Readback); b.Runs.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FailedRampOffRetainsBothClaimsEvenAfterSourceOffAndControllerHold()
    {
        Bench? b = null; b = new(new Listener(_ => { b!.Port.RefuseRampOff = true; return Task.CompletedTask; }), withSource: true);
        var body = new SequenceStep(new("body"), [new SourceOutputStep(new("on"), b.Source!.Id, true), new ReadSourceSenseStep(new("read"), b.Source.Id, SourceFunction.Voltage, new("v"), new("i"))]);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan(body: body)), Token)); Assert.True(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1))); Assert.True(b.Leases.IsLeased(b.Port.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1)));
        b.Port.RefuseRampOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token));
    }
    [Fact]
    public async Task ListenerFaultPreservesCommittedObservationAndStillConfirmsRampOff()
    {
        var error = new IOException("delivery failed"); var b = new Bench(new Listener(_ => Task.FromException(error))); Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan()), Token))); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task LogicalFailureForcedSuccessfulCannotEraseRampOffObligation()
    {
        var b = new Bench(); var step = b.Scan(body: new ResultDecoratorStep(new("failure"), ResultEffect.ForceFailure, b.Read)); await b.Runs.StartPlanAsync(b.Plan(new ResultDecoratorStep(new("forced"), ResultEffect.ForceSuccess, step)), Token); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token));
    }
    [Fact]
    public async Task UnstableFirstPointNeverStartsHardwareRamp()
    {
        var b = new Bench(); b.Port.Freeze = true; var original = b.Scan(stable: true); var step = new HardwareTemperatureScanStep(original.Id, original.Device, original.Column, new([K(4.1), K(4.12)]), original.Rate, true, original.Tolerance, original.StableCount, TimeSpan.FromMilliseconds(100), original.StepTimeout, original.PollInterval, original.Sampling, original.Interval, original.Body);
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(step), Token)); Assert.False(b.Port.Started.Task.IsCompleted); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Port.ReadControlConfigurationAsync(Token)).HardwareRampEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task EstimatedProgressExcludesTimeWhenControllerWasHeldByPause()
    {
        Bench? b = null; var count = 0; var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b = new(new Listener(_ => { if (++count == 1) b!.Runs.Pause(); else second.TrySetResult(); return Task.CompletedTask; })); using var events = b.Hub.Subscribe();
        var original = b.Scan(TemperatureRampSampling.EstimatedSetpoint); var step = new HardwareTemperatureScanStep(original.Id, original.Device, original.Column, new([K(4), K(4.1), K(4.2)]), original.Rate, false, original.Tolerance, original.StableCount, original.StableTimeout, original.StepTimeout, original.PollInterval, original.Sampling, original.Interval, original.Body);
        var running = b.Runs.StartPlanAsync(b.Plan(step), Token); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        await Task.Delay(700); b.Runs.Resume(); await Task.Delay(150); Assert.False(second.Task.IsCompleted); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(3, count); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("target")]
    [InlineData("rate")]
    [InlineData("ramp")]
    [InlineData("control")]
    public async Task ExternalConfigurationDriftStopsBeforeAnotherObservation(string field)
    {
        Bench? b = null; b = new(new Listener(async _ =>
        {
            if (field == "target") await b!.Port.Inner.SetTargetAsync(K(5), Token);
            if (field == "rate") await b!.Port.Inner.SetRateAsync(new(6, PhysicalUnit.KelvinPerMinute), Token);
            if (field == "ramp") await b!.Port.Inner.ConfigureControlAsync(new(false, true), null, Token);
            if (field == "control") await b!.Port.Inner.ConfigureControlAsync(new(true, false), null, Token);
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan()), Token)); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Port.ReadControlConfigurationAsync(Token)).HardwareRampEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task LongSampleIntervalCannotHideTheEndpointGuard()
    {
        var b = new Bench(); var original = b.Scan(TemperatureRampSampling.Timed, timeout: TimeSpan.FromMilliseconds(100)); var scan = new HardwareTemperatureScanStep(original.Id, original.Device, original.Column, original.Points, original.Rate, false, original.Tolerance, original.StableCount, original.StableTimeout, original.StepTimeout, original.PollInterval, original.Sampling, TimeSpan.FromSeconds(30), original.Body);
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(scan), Token).WaitAsync(TimeSpan.FromSeconds(2))); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Port.ReadControlConfigurationAsync(Token)).HardwareRampEnabled);
    }
    [Fact]
    public async Task ParentTimeoutConfirmsRampOffAfterActualChildDrain()
    {
        var b = new Bench(); var scan = b.Scan(body: new DelayStep(new("body"), TimeSpan.FromSeconds(30))); await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(200), scan)), Token)); Assert.False((await b.Port.ReadControlConfigurationAsync(Token)).HardwareRampEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task DynamicSamplingCannotExceedGlobalBudget()
    {
        var b = new Bench(); b.Port.Freeze = true; await Assert.ThrowsAsync<PlanExecutionBudgetException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan(TemperatureRampSampling.Immediate), 8), Token)); Assert.False(b.Runs.HasActiveRun); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token));
    }
    [Theory]
    [InlineData("target")]
    [InlineData("control")]
    public async Task ReconfigurationInChildRejectsBeforeConnection(string kind)
    {
        var b = new Bench(); ExperimentStep body = kind == "target" ? new SetParameterStep(new("write"), new(b.Port.Definition.Device.Id, b.Port.Definition.Target.Id), new ConstantValue(K(5))) : new TemperatureControlStep(new("write"), b.Port.Definition.Device.Id, TemperatureControlAction.RampOff);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Plan(b.Scan(body: body)))); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
}

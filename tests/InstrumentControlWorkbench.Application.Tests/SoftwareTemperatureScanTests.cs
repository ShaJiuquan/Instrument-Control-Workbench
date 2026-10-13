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

public sealed class SoftwareTemperatureScanTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private sealed class Port : ITemperatureConfigurationPort
    {
        public MockAxisController Inner { get; } = MockEnvironmentCatalog.PassiveTemperature();
        public AxisControllerDefinition Definition => Inner.Definition;
        public bool RefuseRampOff { get; set; }
        public bool Freeze { get; set; }
        public bool LateEndpoint { get; set; }
        public bool LateEndpointReturned { get; private set; }
        private int endpointObservations;
        public bool OscillateFirst { get; set; }
        public int FirstObservations { get; private set; }
        public int ObservationsAtEnable { get; private set; }
        private bool firstProgrammed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TemperatureControlConfiguration> Configurations { get; } = [];
        public List<Quantity> Targets { get; } = [];
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken t) => Inner.ConnectAsync(t);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken t)
        {
            var s = await Inner.GetSnapshotAsync(t); if (!s.Connected) return s; var c = await Inner.ReadControlConfigurationAsync(t);
            if (firstProgrammed && !c.HardwareRampEnabled) FirstObservations++;
            if (LateEndpoint && s.Target == K(4.02) && ++endpointObservations == 3) { await Task.Delay(200, t); LateEndpointReturned = true; return s with { Readback = K(4.02) }; }
            return Freeze ? s with { Readback = K(4) } : OscillateFirst && firstProgrammed && !c.HardwareRampEnabled && FirstObservations == 3 ? s with { Readback = K(4.02) } : s;
        }
        public async Task SetTargetAsync(Quantity q, CancellationToken t) { Targets.Add(q); await Inner.SetTargetAsync(q, t); firstProgrammed = true; if (q.Value != 4) Started.TrySetResult(); }
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
        public SoftwareTemperatureScanStep Scan(TemperatureRampSampling sampling = TemperatureRampSampling.Threshold, bool descending = false, bool stable = false, ExperimentStep? body = null, TimeSpan? timeout = null) => new(new("scan"), Port.Definition.Device.Id, new("axis"), new([K(4), K(descending ? 3.99 : 4.01), K(descending ? 3.98 : 4.02)]), new(20, PhysicalUnit.KelvinPerMinute), stable, K(.0005), 3, timeout ?? TimeSpan.FromSeconds(2), timeout ?? TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50), sampling, TimeSpan.FromMilliseconds(20), body ?? new SequenceStep(new("body"), [Read, new DelayStep(new("delay"), TimeSpan.FromMilliseconds(10))]));
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
    public async Task SamplingModesRecordConfirmedSetpointAndActualTemperatureAndPreserveHardwareRate(TemperatureRampSampling mode, bool descending)
    {
        var b = new Bench(); var step = b.Scan(mode, descending); await b.Runs.StartPlanAsync(b.Plan(step), Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.NotEmpty(rows); Assert.All(rows, row => { Assert.Equal(step.ProgressTotal, row.Values[step.TotalColumn].Value); Assert.Equal(PhysicalUnit.Kelvin, row.Values[step.SetpointColumn].Unit); });
        Assert.Equal(step.Points.Values[^1], rows[^1].Values[step.SetpointColumn]); Assert.All(b.Port.Configurations, c => Assert.False(c.HardwareRampEnabled));
        Assert.Equal(new(5, PhysicalUnit.KelvinPerMinute), (await b.Port.GetSnapshotAsync(Token)).Rate);
        if (mode is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint) Assert.Equal(2, rows[^1].Values[step.IndexColumn].Value);
        Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ConfirmedSetpointSamplingDoesNotFabricateActualTemperature()
    {
        var b = new Bench(); b.Port.Freeze = true; var step = b.Scan(TemperatureRampSampling.EstimatedSetpoint); await b.Runs.StartPlanAsync(b.Plan(step), Token);
        Assert.All(b.Runs.LatestDataSet!.Rows, row => Assert.Equal(K(4), row.Values[step.Column])); Assert.Equal(K(4.02), b.Runs.LatestDataSet.Rows[^1].Values[step.SetpointColumn]); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(TemperatureRampSampling.Threshold)]
    [InlineData(TemperatureRampSampling.Timed)]
    [InlineData(TemperatureRampSampling.Immediate)]
    public async Task StationarySensorFaultsInsteadOfClaimingEndpoint(TemperatureRampSampling mode)
    {
        var b = new Bench(); b.Port.Freeze = true; await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan(mode, timeout: TimeSpan.FromMilliseconds(100))), Token)); Assert.False(b.Runs.HasActiveRun); Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.ReadControlConfigurationAsync(Token));
    }
    [Fact]
    public async Task OnTimeArrivalDuringLongTimedIntervalDoesNotBecomeAFakeTimeout()
    {
        var b = new Bench(); var o = b.Scan(TemperatureRampSampling.Timed, timeout: TimeSpan.FromMilliseconds(400));
        var scan = new SoftwareTemperatureScanStep(o.Id, o.Device, o.Column, o.Points, o.Rate, false, o.Tolerance, o.StableCount, o.StableTimeout, o.StepTimeout, o.PollInterval, o.Sampling, TimeSpan.FromMilliseconds(900), o.Body);
        await b.Runs.StartPlanAsync(b.Plan(scan), Token).WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.True(b.Runs.LatestDataSet.Rows[1].Timestamp - b.Runs.LatestDataSet.Rows[0].Timestamp >= TimeSpan.FromMilliseconds(880)); Assert.True(b.Runs.LatestDataSet.Rows[^1].Values[scan.Column].Value >= 4.0195); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(TemperatureRampSampling.Threshold)]
    [InlineData(TemperatureRampSampling.EstimatedSetpoint)]
    public async Task EndpointRoundedShortOfRawThresholdTimesOutWithoutInventingArrival(TemperatureRampSampling mode)
    {
        var b = new Bench(); var o = b.Scan(mode, timeout: TimeSpan.FromMilliseconds(800));
        var scan = new SoftwareTemperatureScanStep(o.Id, o.Device, o.Column, new([K(4), K(4.0204)]), o.Rate, false, o.Tolerance, o.StableCount, o.StableTimeout, o.StepTimeout, o.PollInterval, o.Sampling, o.Interval, o.Body);
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(scan), Token)); Assert.Contains(K(4.02), b.Port.Targets); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FirstEndpointReadReturningAfterTailDeadlineCannotClaimSuccess()
    {
        var b = new Bench(); b.Port.Freeze = true; b.Port.LateEndpoint = true;
        var scan = b.Scan(TemperatureRampSampling.Timed, timeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(scan), Token));
        Assert.True(b.Port.LateEndpointReturned); Assert.NotEmpty(b.Runs.LatestDataSet!.Rows);
        Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Equal(K(4), row.Values[scan.Column])); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task LongTimedIntervalStillAdvancesSetpointAndChecksEndpointDeadline()
    {
        var b = new Bench(); b.Port.Freeze = true; var o = b.Scan(TemperatureRampSampling.Timed, timeout: TimeSpan.FromMilliseconds(100)); var scan = new SoftwareTemperatureScanStep(o.Id, o.Device, o.Column, o.Points, o.Rate, false, o.Tolerance, o.StableCount, o.StableTimeout, o.StepTimeout, o.PollInterval, o.Sampling, TimeSpan.FromSeconds(30), o.Body);
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(scan), Token).WaitAsync(TimeSpan.FromSeconds(3))); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Contains(K(4.02), b.Port.Targets); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task SoftwareSetpointDoesNotAdvanceInBackgroundDuringChildMeasurement()
    {
        Bench? b = null; b = new(new Listener(async _ => { var before = b!.Port.Targets.Count; await Task.Delay(120); Assert.Equal(before, b.Port.Targets.Count); }));
        await b.Runs.StartPlanAsync(b.Plan(b.Scan(TemperatureRampSampling.EstimatedSetpoint)), Token); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task HeldPauseIsExcludedFromTheSoftwareTrajectory()
    {
        Bench? b = null; var count = 0; var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b = new(new Listener(_ => { if (++count == 1) b!.Runs.Pause(); else second.TrySetResult(); return Task.CompletedTask; })); using var events = b.Hub.Subscribe();
        var o = b.Scan(TemperatureRampSampling.EstimatedSetpoint); var scan = new SoftwareTemperatureScanStep(o.Id, o.Device, o.Column, new([K(4), K(4.1), K(4.2)]), o.Rate, false, o.Tolerance, o.StableCount, o.StableTimeout, o.StepTimeout, o.PollInterval, o.Sampling, o.Interval, o.Body);
        var running = b.Runs.StartPlanAsync(b.Plan(scan), Token); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        var writes = b.Port.Targets.Count; await Task.Delay(700); Assert.Equal(writes, b.Port.Targets.Count); b.Runs.Resume(); await Task.Delay(150); Assert.False(second.Task.IsCompleted); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(3, count); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("target")]
    [InlineData("rate")]
    [InlineData("ramp")]
    [InlineData("control")]
    public async Task ExternalDriftIsRejectedBeforeTheNextSetpointWrite(string field)
    {
        Bench? b = null; var writes = 0; b = new(new Listener(async _ =>
        {
            writes = b!.Port.Targets.Count;
            if (field == "target") await b.Port.Inner.SetTargetAsync(K(5), Token);
            if (field == "rate") await b.Port.Inner.SetRateAsync(new(6, PhysicalUnit.KelvinPerMinute), Token);
            if (field == "ramp") await b.Port.Inner.ConfigureControlAsync(new(true, true), null, Token);
            if (field == "control") await b.Port.Inner.ConfigureControlAsync(new(false, false), null, Token);
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan()), Token)); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(writes, b.Port.Targets.Count); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ListenerFailureKeepsCommittedEvidenceAndConfirmsStop()
    {
        var error = new IOException("delivery failed"); var b = new Bench(new Listener(_ => Task.FromException(error)));
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan()), Token))); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FailedRampOffAfterSoftwareFaultRetainsTheWholeLeaseGroup()
    {
        Bench? b = null; b = new(new Listener(_ => { b!.Port.RefuseRampOff = true; throw new IOException("delivery failed"); }), withSource: true);
        var body = new SequenceStep(new("body"), [new SourceOutputStep(new("on"), b.Source!.Id, true), new ReadSourceSenseStep(new("read"), b.Source.Id, SourceFunction.Voltage, new("v"), new("i"))]);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan(body: body)), Token)); Assert.True(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1))); Assert.True(b.Leases.IsLeased(b.Port.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1)));
        b.Port.RefuseRampOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task AbortWhilePausedStillRequiresAFreshTerminalRampOffProof()
    {
        Bench? b = null; b = new(new Listener(_ => { b!.Runs.Pause(); return Task.CompletedTask; })); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Scan()), Token); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        b.Port.RefuseRampOff = true; b.Runs.Abort(); await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(b.Runs.HasActiveRun); b.Port.RefuseRampOff = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task UnstableFirstPointDoesNotActivateTheSoftwareTrajectory()
    {
        var b = new Bench(); b.Port.Freeze = true; var o = b.Scan(); var step = new SoftwareTemperatureScanStep(o.Id, o.Device, o.Column, new([K(4.1), K(4.12)]), o.Rate, true, o.Tolerance, o.StableCount, TimeSpan.FromMilliseconds(100), o.StepTimeout, o.PollInterval, o.Sampling, o.Interval, o.Body);
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(step), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.DoesNotContain(K(4.12), b.Port.Targets); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ParentTimeoutDrainsChildAndConfirmsSafeStop()
    {
        var b = new Bench(); var scan = b.Scan(body: new DelayStep(new("body"), TimeSpan.FromSeconds(30))); await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(150), scan)), Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ImmediateSamplingCannotExceedGlobalExecutionBudget()
    {
        var b = new Bench(); await Assert.ThrowsAsync<PlanExecutionBudgetException>(() => b.Runs.StartPlanAsync(b.Plan(b.Scan(TemperatureRampSampling.Immediate), 8), Token)); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("target")]
    [InlineData("control")]
    [InlineData("nested-software")]
    [InlineData("nested-hardware")]
    public async Task ControllerMutationInChildrenRejectsBeforeConnection(string kind)
    {
        var b = new Bench(); var o = b.Scan(); ExperimentStep child = kind switch
        {
            "target" => new SetParameterStep(new("write"), new(o.Device, b.Port.Definition.Target.Id), new ConstantValue(K(5))),
            "control" => new TemperatureControlStep(new("write"), o.Device, TemperatureControlAction.RampOff),
            "nested-software" => new SoftwareTemperatureScanStep(new("inner"), o.Device, new("inner-axis"), o.Points, o.Rate, false, o.Tolerance, o.StableCount, o.StableTimeout, o.StepTimeout, o.PollInterval, o.Sampling, o.Interval, b.Read),
            _ => new HardwareTemperatureScanStep(new("inner"), o.Device, new("inner-axis"), o.Points, o.Rate, false, o.Tolerance, o.StableCount, o.StableTimeout, o.StepTimeout, o.PollInterval, o.Sampling, o.Interval, b.Read),
        };
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Plan(b.Scan(body: child)))); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
}

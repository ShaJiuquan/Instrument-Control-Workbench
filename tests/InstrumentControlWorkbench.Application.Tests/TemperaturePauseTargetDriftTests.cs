using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class TemperaturePauseTargetDriftTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);

    [Theory]
    [InlineData("software_scan")]
    [InlineData("hardware_scan")]
    [InlineData("ordinary")]
    public async Task ExternalPausedTargetDriftRefusesResumeBeforeAnyTargetOrSourceRestoration(string mode)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(mode), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused);
        var writes = b.Port.TargetWrites.Count;
        b.Port.Target = K(5);
        b.Runs.Resume();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("temperature target changed while paused", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(writes, b.Port.TargetWrites.Count);
        Assert.Equal(2, b.Port.Holds);
        Assert.Equal(1, b.Port.RampOffAttempts);
        Assert.False(b.Port.Configuration.HardwareRampEnabled);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("software_scan")]
    [InlineData("hardware_scan")]
    [InlineData("ordinary")]
    public async Task DriftAndFailedFreshRampOffRetainAllClaimsUntilExplicitRecovery(string mode)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(mode), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused);
        var writes = b.Port.TargetWrites.Count;
        b.Port.Target = K(5); b.Port.RefuseRampOff = true;
        b.Runs.Resume();
        var failure = await Assert.ThrowsAsync<AggregateException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(failure.InnerExceptions, e => e is InvalidOperationException && e.Message.Contains("temperature target changed while paused", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failure.InnerExceptions, e => e is IOException && e.Message == "ramp OFF refused");
        Assert.Equal(writes, b.Port.TargetWrites.Count);
        Assert.Equal(2, b.Port.Holds);
        Assert.Equal(1, b.Port.RampOffAttempts);
        Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        Assert.True(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Target.Id, K(4), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(.002, PhysicalUnit.Volt), Token));
        b.Port.RefuseRampOff = false;
        await b.Runs.RecoverSafetyAsync(Token);
        Assert.Equal(2, b.Port.RampOffAttempts);
        Assert.Equal(3, b.Port.Holds);
        Assert.False(b.Runs.HasActiveRun);
        Assert.False(b.Port.Configuration.HardwareRampEnabled);
        await b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Target.Id, K(4), Token);
        await b.Devices.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(.002, PhysicalUnit.Volt), Token);
    }

    [Theory]
    [InlineData("software_scan")]
    [InlineData("hardware_scan")]
    [InlineData("ordinary")]
    public async Task ConfirmedHoldTargetMayDifferFromPrePauseTargetAndReadbackMayDriftNaturally(string mode)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(mode), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.NotEqual(b.Port.PreHoldTarget, b.Port.Target);
        var targetToRestore = b.Port.PreHoldTarget;
        var writes = b.Port.TargetWrites.Count;
        b.Port.Readback = K(3.991);
        Assert.NotEqual(b.Port.Target, b.Port.Readback);
        b.Runs.Resume();
        await Until(events, WorkbenchNotificationKind.RunResumed);
        Assert.Equal(targetToRestore, b.Port.TargetWrites[writes]);
        b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task DriftBetweenResourcePrecheckAndSessionRestorationIsAlsoRefused()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan("ordinary"), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused);
        var writes = b.Port.TargetWrites.Count;
        // Resource precheck reads flags once, then the status observation reads them twice.
        // The fourth read is the session's immediate pre-write check.
        b.Port.DriftOnFourthConfigurationRead = true;
        b.Runs.Resume();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Temperature target changed while paused", error.Message);
        Assert.Equal(writes, b.Port.TargetWrites.Count);
        Assert.Equal(1, b.Port.RampOffAttempts);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task LegacyPortWithoutIndependentRampOffKeepsOwnershipAfterTargetDrift()
    {
        var b = new Bench(legacy: true); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan("ordinary"), Token);
        await Until(events, WorkbenchNotificationKind.RunPaused);
        var writes = b.Port.TargetWrites.Count;
        b.Port.Target = K(5); b.Runs.Resume();
        var failure = await Assert.ThrowsAsync<AggregateException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(failure.InnerExceptions, e => e is NotSupportedException);
        Assert.Equal(writes, b.Port.TargetWrites.Count);
        Assert.True(b.Runs.HasActiveRun);
        Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        await Assert.ThrowsAsync<NotSupportedException>(() => b.Runs.RecoverSafetyAsync(Token));
        Assert.True(b.Runs.HasActiveRun);
        Assert.Equal(0, b.Port.RampOffAttempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(.002, PhysicalUnit.Volt), Token));
    }

    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }

    private sealed class Bench : IRunResultListener
    {
        public Port Port { get; } = new();
        public MockKeithley2450SourceMeter Source { get; } = new();
        public NotificationHub Hub { get; } = new();
        public DeviceRegistry Devices { get; }
        public MeasurementRunService Runs { get; }
        private bool paused;
        public Bench(bool legacy = false)
        {
            var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            var source = new InstrumentManager(Source, Hub);
            Devices = new([source, new AxisControllerSession(legacy ? new LegacyPort(Port) : Port, Hub)], leases);
            Runs = new(source, leases, Hub, [this], deviceRegistry: Devices);
        }
        public ExperimentPlan Plan(string mode)
        {
            var read = new ReadStep(new("read"), [new(new("temperature"), new(Port.Definition.Device.Id, Port.Definition.Readback.Id))]);
            var body = new SequenceStep(new("body"), [read, new DelayStep(new("delay"), TimeSpan.FromSeconds(30))]);
            var points = new ScanValues([K(4), K(4.01), K(4.02)]);
            var rate = new Quantity(20, PhysicalUnit.KelvinPerMinute);
            var timeout = TimeSpan.FromSeconds(2);
            ExperimentStep step = mode switch
            {
                "software_scan" => new SoftwareTemperatureScanStep(new("scan"), Port.Definition.Device.Id, new("axis"), points, rate, false, K(.001), 3, timeout, timeout, TimeSpan.FromMilliseconds(50), TemperatureRampSampling.Threshold, TimeSpan.FromMilliseconds(20), body),
                "hardware_scan" => new HardwareTemperatureScanStep(new("scan"), Port.Definition.Device.Id, new("axis"), points, rate, false, K(.001), 3, timeout, timeout, TimeSpan.FromMilliseconds(50), TemperatureRampSampling.Threshold, TimeSpan.FromMilliseconds(20), body),
                "ordinary" => new SequenceStep(new("temperature"), [new SetParameterStep(new("set"), new(Port.Definition.Device.Id, Port.Definition.Target.Id), new ConstantValue(K(5))), body]),
                _ => throw new ArgumentException("Unknown mode.")
            };
            return new("Paused target proof", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), step]),
                [new(Source.Id, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        }
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken t) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken t)
        {
            if (!paused) { paused = true; Runs.Pause(); }
            return Task.CompletedTask;
        }
        public Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken t) => Task.CompletedTask;
    }

    // Expose the same controller through the older ramp-only capability contract.
    private sealed class LegacyPort(Port inner) : ITemperatureControlPort
    {
        public AxisControllerDefinition Definition => inner.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken t) => inner.ConnectAsync(t);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken t) => inner.GetSnapshotAsync(t);
        public Task SetTargetAsync(Quantity value, CancellationToken t) => inner.SetTargetAsync(value, t);
        public Task SetRateAsync(Quantity value, CancellationToken t) => inner.SetRateAsync(value, t);
        public Task HoldAsync(CancellationToken t) => inner.HoldAsync(t);
        public Task DisconnectAsync(CancellationToken t) => inner.DisconnectAsync(t);
        public Task PrepareSoftwareRampAsync(Quantity origin, CancellationToken t) => inner.PrepareSoftwareRampAsync(origin, t);
        public Task SetHardwareRampAsync(Quantity value, CancellationToken t) => inner.SetHardwareRampAsync(value, t);
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken t) => inner.ReadControlConfigurationAsync(t);
    }

    private sealed class Port : ITemperatureConfigurationPort
    {
        public AxisControllerDefinition Definition { get; } = MockEnvironmentCatalog.Temperature().Definition;
        public bool Connected { get; private set; }
        public Quantity Target { get; set; } = K(4);
        public Quantity Readback { get; set; } = K(4);
        private Quantity rate = new(5, PhysicalUnit.KelvinPerMinute);
        public TemperatureControlConfiguration Configuration { get; private set; } = new(true, true);
        public List<Quantity> TargetWrites { get; } = [];
        public Quantity PreHoldTarget { get; private set; }
        public int Holds { get; private set; }
        public int RampOffAttempts { get; private set; }
        public bool RefuseRampOff { get; set; }
        public bool DriftOnFourthConfigurationRead { get; set; }
        private int resumeConfigurationReads;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); Connected = true; return GetSnapshotAsync(t); }
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken t)
        {
            t.ThrowIfCancellationRequested();
            return Task.FromResult(new AxisControllerSnapshot(Connected, Target, Readback, rate, Target != Readback, DateTimeOffset.UtcNow));
        }
        public Task SetTargetAsync(Quantity value, CancellationToken t) { t.ThrowIfCancellationRequested(); Target = value; TargetWrites.Add(value); return Task.CompletedTask; }
        public Task SetRateAsync(Quantity value, CancellationToken t) { t.ThrowIfCancellationRequested(); rate = value; return Task.CompletedTask; }
        public Task PrepareSoftwareRampAsync(Quantity origin, CancellationToken t) { t.ThrowIfCancellationRequested(); Target = origin; Configuration = new(false, true); return Task.CompletedTask; }
        public Task SetHardwareRampAsync(Quantity value, CancellationToken t) { t.ThrowIfCancellationRequested(); rate = value; Configuration = new(true, true); return Task.CompletedTask; }
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken t)
        {
            t.ThrowIfCancellationRequested();
            if (DriftOnFourthConfigurationRead && ++resumeConfigurationReads == 4) { Target = K(5); DriftOnFourthConfigurationRead = false; }
            return Task.FromResult(Configuration);
        }
        public Task ConfigureControlAsync(TemperatureControlConfiguration configuration, Quantity? requestedRate, CancellationToken t)
        {
            t.ThrowIfCancellationRequested();
            if (!configuration.HardwareRampEnabled && Holds > 0) { RampOffAttempts++; if (RefuseRampOff) throw new IOException("ramp OFF refused"); }
            Configuration = configuration; if (requestedRate is { } value) rate = value;
            return Task.CompletedTask;
        }
        public Task HoldAsync(CancellationToken t)
        {
            t.ThrowIfCancellationRequested(); PreHoldTarget = Target;
            if (Holds++ == 0) Readback = K(3.99);
            Target = Readback;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); Connected = false; return Task.CompletedTask; }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
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
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class SoftwareTemperatureExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private static Quantity Rate(double value) => new(value, PhysicalUnit.KelvinPerMinute);
    [Fact]
    public async Task RampStartsFromPhysicalTemperatureInsteadOfExistingTargetAndHasNoFinalTick()
    {
        var b = new Bench(); var execution = b.Runs.StartPlanAsync(b.Plan(300.05, 5), Token);
        Assert.True(execution.IsCompletedSuccessfully); await execution;
        Assert.Equal(300, b.Port.ParkedAt!.Value.Value); Assert.Equal(new[] { 300.05 }, b.Port.Writes.Select(v => v.Value));
        Assert.Equal(1, b.Port.Holds);
        Assert.Equal(new TemperatureControlConfiguration(false, true), b.Port.Configuration);
        Assert.Equal(K(300), b.Port.Target); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
        var header = Assert.Single(b.Runs.LatestDataSet!.Instruments); Assert.Equal("True", header.Configuration["hardware_ramp_enabled"]);
    }
    [Fact]
    public async Task MultipleUpdatesUseOneSecondTicksAndClampFinalTarget()
    {
        var b = new Bench(); await b.Runs.StartPlanAsync(b.Plan(300.2, 5), Token);
        Assert.Equal(new[] { 300.083, 300.166, 300.2 }, b.Port.Writes.Select(q => q.Value));
        var ticks = b.Port.WriteTimes.ToArray(); Assert.True(ticks[1] - ticks[0] >= TimeSpan.FromMilliseconds(950));
        Assert.True(ticks[2] - ticks[1] >= TimeSpan.FromMilliseconds(950));
    }
    [Theory]
    [InlineData(null, 300.0007)]
    [InlineData(5.0, 300.001)]
    public async Task DirectSetsPreservePrecisionAndPositiveRatesRoundMilliKelvin(double? rate, double expected)
    {
        var b = new Bench(); await b.Runs.StartPlanAsync(b.Plan(300.0007, rate), Token);
        Assert.Equal(expected, Assert.Single(b.Port.Writes).Value); Assert.False(b.Port.Configuration.HardwareRampEnabled);
    }
    [Fact]
    public async Task RuntimeVariableRangeFailsBeforeModeOrTargetMutation()
    {
        var b = new Bench(); var root = new SequenceStep(new("root"), [new AssignValueStep(new("assign"), new("target"), new ConstantValue(K(400))),
            new SoftwareTemperatureRampStep(new("ramp"), b.Port.Definition.Device.Id, new VariableValue(new("target")), Rate(5))]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(new("Bad variable", root), Token));
        Assert.Null(b.Port.ParkedAt); Assert.Empty(b.Port.Writes); Assert.Equal(1, b.Port.Holds); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task UnreadableLiveOriginNeverFallsBackToOldTargetOrRequestedEndpoint()
    {
        var b = new Bench(); b.OnHeader = () => b.Port.FailSnapshotOnce = true;
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(301, 5), Token));
        Assert.Contains("live read failed", error.Message); Assert.Null(b.Port.ParkedAt); Assert.Empty(b.Port.Writes);
        Assert.Equal(1, b.Port.Holds); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("prepare")]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("write")]
    public async Task UnconfirmedModeDriftAndWriteFailureStopRampAndConfirmHold(string fault)
    {
        var b = new Bench(); b.Port.Fault = fault;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(301, 5), Token));
        Assert.Equal(1, b.Port.Holds); Assert.Equal(K(300), b.Port.Target); Assert.False(b.Runs.HasActiveRun);
        Assert.Equal(fault == "during" ? 1 : 0, b.Port.Writes.Count);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
    }
    [Fact]
    public async Task PauseKeepsReservationAndAbortDoesNotProgramTheEndpoint()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var run = b.Runs.StartPlanAsync(b.Plan(310, 5), Token);
        await b.Port.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.Equal(K(300), b.Port.Target); Assert.False(b.Port.Configuration.HardwareRampEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Target.Id, K(301), Token));
        b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(b.Port.Writes);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.False(b.Runs.HasActiveRun);
        await b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Target.Id, K(301), Token);
    }
    [Fact]
    public async Task PauseResumePreservesRemainingTickAndRestoresProgrammedTargetBeforeContinuing()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var run = b.Runs.StartPlanAsync(b.Plan(300.2, 5), Token);
        await b.Port.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        var before = b.Port.Writes.Count; await Task.Delay(150); Assert.Equal(before, b.Port.Writes.Count);
        var resumed = Stopwatch.StartNew(); b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 300.083, 300.083, 300.166, 300.2 }, b.Port.Writes.Select(q => q.Value));
        Assert.True(resumed.Elapsed >= TimeSpan.FromMilliseconds(1800)); Assert.Equal(2, b.Port.Holds);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PausedTemperatureModeOrControlDriftRefusesAllRestoration(bool hardwareRamp, bool control)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var run = b.Runs.StartPlanAsync(b.SourceAndTemperaturePlan(), Token);
        await b.Port.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        b.Port.Configuration = new(hardwareRamp, control); b.Runs.Resume();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("temperature control changed", error.Message); Assert.Single(b.Port.Writes); Assert.Equal(2, b.Port.Holds);
        Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task FailedTemperatureHoldRetainsReservationUntilExplicitRecovery()
    {
        var b = new Bench(); b.Port.FailHold = true;
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(300.05, 5), Token));
        Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase); Assert.True(b.Runs.HasActiveRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Target.Id, K(301), Token));
        b.Port.FailHold = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun); Assert.Equal(K(300), b.Port.Target);
    }
    [Fact]
    public void MissingCapabilityUndefinedVariablesAndExcessiveRampBudgetsFailWithoutIo()
    {
        var b = new Bench(); var field = MockEnvironmentCatalog.Field();
        var session = new AxisControllerSession(field, b.Hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var source = new InstrumentManager(new MockKeithley2450SourceMeter(), b.Hub);
        var runs = new MeasurementRunService(source, leases, b.Hub, deviceRegistry: new DeviceRegistry([source, session], leases));
        Assert.Throws<NotSupportedException>(() => runs.PreviewPlan(new("Bad capability", new SoftwareTemperatureRampStep(new("r"), field.Definition.Device.Id, new ConstantValue(K(300))))));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Undefined", new SoftwareTemperatureRampStep(new("r"), b.Port.Definition.Device.Id, new VariableValue(new("unset"))))));
        var expensive = new ExperimentPlan("Cost", new RepeatStep(new("repeat"), 40, b.Plan(310, 0.01).Root));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Runs.PreviewPlan(expensive)); Assert.Null(b.Port.ParkedAt); Assert.False(b.Port.Connected);
    }
    [Fact]
    public async Task HardwareRateWriteConfirmsModeAndRefusesAnUnacknowledgedController()
    {
        var b = new Bench(); await b.Devices.ConnectAsync(b.Port.Definition.Device.Id, Token);
        await b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Rate.Id, Rate(10), Token);
        Assert.Equal(new TemperatureControlConfiguration(true, true), b.Port.Configuration); Assert.Equal(Rate(10), b.Port.CurrentRate);
        b.Port.IgnoreHardwareRamp = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.WriteAsync(b.Port.Definition.Device.Id, b.Port.Definition.Rate.Id, Rate(11), Token));
    }
    [Fact]
    public async Task NativePlanArchiveAndCsvRetainRampPolicyAndRequestedVersusMeasuredTemperature()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"temperature-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var ramp = b.Plan(300.05, 5).Root;
                var plan = new ExperimentPlan("Temperature and readback", new ForEachStep(new("loop"), new("requested.temperature"), new([K(300.05)]),
                    new SequenceStep(new("point"), [ramp, new ReadStep(new("read"), [new(new("measured.temperature"), new(b.Port.Definition.Device.Id, b.Port.Definition.Readback.Id))])])));
                var codec = new JsonExperimentPlanDocument(); var path = Path.Combine(folder, "ramp.plan.json");
                await codec.SaveAsync(path, plan, Token); var decoded = await codec.LoadAsync(path, Token); await b.Runs.StartPlanAsync(decoded, Token);
                var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(K(300.05), row.Values[new("requested.temperature")]);
                Assert.Equal(K(300), row.Values[new("measured.temperature")]); id = b.Runs.LatestDataSet.Id;
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(id, Token);
            var native = new JsonExperimentPlanDocument().Serialize(stored.DataSet.Plan!); Assert.Contains("software_temperature_ramp", native); Assert.Contains("KelvinPerMinute", native);
            Assert.Equal("True", Assert.Single(stored.DataSet.Instruments).Configuration["temperature_control_enabled"]);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Single(fresh.Runs.LatestDataSet!.Rows);
            var csv = Path.Combine(folder, "temperature.csv"); await reopened.ExportCsvAsync(id, csv, Token);
            Assert.Contains("measured.temperature", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class Bench : IRunResultListener
    {
        public TemperatureProbe Port { get; } = new(); public NotificationHub Hub { get; } = new();
        public MockKeithley2450SourceMeter Source { get; } = new();
        public DeviceRegistry Devices { get; }
        public MeasurementRunService Runs { get; }
        public Action? OnHeader { get; set; }
        public Bench(IRunResultListener? archive = null)
        {
            var source = new InstrumentManager(Source, Hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            Devices = new([source, new AxisControllerSession(Port, Hub)], leases);
            Runs = new(source, leases, Hub, archive is null ? [this] : [this, archive], deviceRegistry: Devices);
        }
        public ExperimentPlan Plan(double target, double? rate) => new("Software temperature", new SoftwareTemperatureRampStep(new("ramp"), Port.Definition.Device.Id, new ConstantValue(K(target)), rate is { } r ? Rate(r) : null));
        public ExperimentPlan SourceAndTemperaturePlan() => new("Source and temperature",
            new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), Plan(310, 5).Root]),
            [new(Source.Id, SourceFunction.Voltage, new(0.01, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) { OnHeader?.Invoke(); return Task.CompletedTask; }
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) => Task.CompletedTask;
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Task.CompletedTask;
    }
    // This controllable port models a lagging physical readback, separately from
    // programmed targets. Faults exercise actual session/ownership boundaries.
    private sealed class TemperatureProbe : ITemperatureControlPort
    {
        public AxisControllerDefinition Definition { get; } = MockEnvironmentCatalog.Temperature().Definition;
        public bool Connected { get; private set; }
        public Quantity Target { get; private set; } = K(305);
        public Quantity CurrentRate { get; private set; } = Rate(5);
        public TemperatureControlConfiguration Configuration { get; set; } = new(true, true);
        public Quantity? ParkedAt { get; private set; }
        public ConcurrentQueue<Quantity> Writes { get; } = new();
        public ConcurrentQueue<TimeSpan> WriteTimes { get; } = new();
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Holds { get; private set; }
        public bool FailSnapshotOnce { get; set; }
        public string? Fault { get; set; }
        public bool IgnoreHardwareRamp { get; set; }
        public bool FailHold { get; set; }
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool prepared;
        private int preparedConfigurationReads;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Connected = true; return GetSnapshotAsync(token); }
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailSnapshotOnce) { FailSnapshotOnce = false; throw new IOException("live read failed"); }
            return Task.FromResult(new AxisControllerSnapshot(Connected, Target, K(300), CurrentRate, Target != K(300), DateTimeOffset.UtcNow));
        }
        public Task PrepareSoftwareRampAsync(Quantity initial, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ParkedAt = initial; Target = initial; prepared = true;
            Configuration = new(false, Fault != "prepare"); return Task.CompletedTask;
        }
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (prepared && ++preparedConfigurationReads > 2 && Fault == "before") { Fault = null; return Task.FromResult(new TemperatureControlConfiguration(true, true)); }
            return Task.FromResult(Configuration);
        }
        public Task SetTargetAsync(Quantity target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Definition.Target.ValidateWrite(target);
            if (Fault == "write") throw new IOException("target write failed");
            Target = target; Writes.Enqueue(target); WriteTimes.Enqueue(clock.Elapsed); FirstWrite.TrySetResult();
            if (Fault == "during") Configuration = new(true, true); return Task.CompletedTask;
        }
        public Task SetHardwareRampAsync(Quantity rate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (!IgnoreHardwareRamp) { CurrentRate = rate; Configuration = new(true, true); }
            return Task.CompletedTask;
        }
        public Task SetRateAsync(Quantity rate, CancellationToken token) => throw new InvalidOperationException("Temperature hardware rate must use the temperature-control port.");
        public Task HoldAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); if (FailHold) throw new IOException("temperature hold failed"); Target = K(300); Holds++; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Connected = false; return Task.CompletedTask; }
    }
}

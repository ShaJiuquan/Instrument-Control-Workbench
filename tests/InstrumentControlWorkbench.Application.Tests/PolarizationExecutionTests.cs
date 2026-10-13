using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PolarizationExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private sealed class Bench
    {
        public Driver Port { get; } = new(); public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null)
        { Registry = new([new InstrumentManager(Port, Hub)], Leases); Runs = new(Registry, Hub, archive is null ? null : [archive]); }
        public ReadPolarizationStep Pulse(TimeSpan? pulse = null, TimeSpan? inter = null, TimeSpan? read = null, int maximum = 8) => new(new("pulse"), Port.Id, new(5, PhysicalUnit.Volt), pulse ?? TimeSpan.Zero, new(0, PhysicalUnit.Volt), inter ?? TimeSpan.Zero, new(1, PhysicalUnit.Volt), read ?? TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true), new("v"), new("i"), new("attempts"), maximum);
        public ExperimentPlan Plan(ExperimentStep root) => new("Polarization", root, [new(Port.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
    }
    private sealed class Driver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public Core.Devices.DeviceDescriptor Descriptor => inner.Descriptor;
        public List<(double Voltage, bool Enabled)> Writes { get; } = [];
        public List<double> EnabledAt { get; } = [];
        public int Frames { get; private set; }
        public int Stops { get; private set; }
        public bool StopTokenLive { get; private set; }
        public bool RefuseStopAfterFrame { get; set; }
        public bool RefuseStageWrite { get; set; }
        public bool RefuseFrame { get; set; }
        public Func<Task>? AfterFrame { get; set; }
        public Func<SourceSenseReading, SourceSenseReading>? Map { get; set; }
        public TaskCompletionSource Enabled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InterWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken t) => inner.ConnectAsync(t);
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken t) => inner.GetSnapshotAsync(t);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken t) => inner.HeartbeatAsync(t);
        public async Task ConfigureSourceAsync(SourceSetpoint s, CancellationToken t)
        {
            var before = await inner.GetSnapshotAsync(t); Writes.Add((s.Level, before.OutputEnabled));
            if (RefuseStageWrite && before.OutputEnabled) throw new IOException("Stage write failed.");
            await inner.ConfigureSourceAsync(s, t);
            if (before.OutputEnabled && s.Level == 0) InterWritten.TrySetResult();
            if (before.OutputEnabled && s.Level == 1) ReadWritten.TrySetResult();
        }
        public Task ConfigureSenseAsync(SenseConfiguration s, CancellationToken t) => inner.ConfigureSenseAsync(s, t);
        public async Task EnableOutputAsync(CancellationToken t) { EnabledAt.Add((await inner.GetSnapshotAsync(t)).Source.Level); await inner.EnableOutputAsync(t); Enabled.TrySetResult(); }
        public Task DisableOutputAsync(CancellationToken t) => inner.DisableOutputAsync(t);
        public async Task SafeStopAsync(CancellationToken t)
        { Stops++; StopTokenLive = !t.IsCancellationRequested; if (RefuseStopAfterFrame && Frames != 0) throw new IOException("OFF confirmation failed."); await inner.SafeStopAsync(t); }
        public Task DisconnectAsync(CancellationToken t) => inner.DisconnectAsync(t);
        public Task<MeasurementValue> ReadAsync(CancellationToken t) => throw new InvalidOperationException("Independent readings are forbidden.");
        public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken t)
        {
            Frames++; if (RefuseFrame) throw new IOException("Read failed.");
            var frame = await inner.ReadSourceSenseAsync(t); if (AfterFrame is { } callback) await callback(); return Map?.Invoke(frame) ?? frame;
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequenceSetsFirstVoltageWhileOffAndPublishesOnlyAfterOff(bool inter)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var pulse = b.Pulse(inter: inter ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero);
        await b.Runs.StartPlanAsync(b.Plan(pulse), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(new[] { 5.0 }, b.Port.EnabledAt); Assert.Contains((5.0, false), b.Port.Writes); Assert.Contains((1.0, true), b.Port.Writes);
        Assert.Equal(inter, b.Port.Writes.Contains((0, true))); Assert.Equal(1, b.Port.Frames);
        Assert.Equal(new(1, PhysicalUnit.Volt), row.Values[pulse.VoltageColumn]); Assert.Equal(new(1, PhysicalUnit.Dimensionless), row.Values[pulse.AttemptsColumn]);
        Assert.Equal(2, row.Quality.Count); Assert.Equal(row.Quality[0].CapturedAt, row.Quality[1].CapturedAt);
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("pulse")]
    [InlineData("inter")]
    [InlineData("read")]
    public async Task AbortDuringAnyDwellDiscardsSampleAndStopsWithIndependentToken(string stage)
    {
        var b = new Bench(); var pulse = b.Pulse(pulse: stage == "pulse" ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, inter: stage == "inter" ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, read: stage == "read" ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        var running = b.Runs.StartPlanAsync(b.Plan(pulse), Token);
        await (stage switch { "pulse" => b.Port.Enabled.Task, "inter" => b.Port.InterWritten.Task, _ => b.Port.ReadWritten.Task }).WaitAsync(TimeSpan.FromSeconds(5));
        b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(0, b.Port.Frames);
        Assert.True(b.Port.StopTokenLive); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("drift")]
    [InlineData("stale")]
    public async Task StageFaultDriftAndStaleFrameCannotProduceSample(string failure)
    {
        var b = new Bench(); b.Port.RefuseStageWrite = failure == "write"; b.Port.RefuseFrame = failure == "read";
        if (failure == "drift") b.Port.AfterFrame = async () => { var s = await b.Port.GetSnapshotAsync(Token); await b.Port.ConfigureSenseAsync(s.Sense with { Nplc = 2 }, Token); };
        if (failure == "stale") b.Port.Map = f => new(f.Function, f.SenseFunction, f.Source, f.Sense, f.Timestamp.AddHours(-1), f.Status, f.AcquisitionConfiguration);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Pulse()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FailedFinalOffSuppressesRowAndRetainsOwnershipUntilRecovery()
    {
        var b = new Bench(); b.Port.RefuseStopAfterFrame = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Pulse()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        Assert.True(b.Leases.IsLeased(b.Port.Id, DateTimeOffset.UtcNow.AddYears(1))); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Port.Id, Keithley2450Parameters.MeasuredCurrent.Id, Token));
        b.Port.RefuseStopAfterFrame = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task TimeoutDuringPulseStopsAndDiscardsSample()
    {
        var b = new Bench(); var root = new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(30), b.Pulse(pulse: TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(b.Plan(root), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PauseKeepsOffAndResumeRestartsWholePulseWithBoundedAttempts(int maximum)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Pulse(pulse: TimeSpan.FromMilliseconds(100), maximum: maximum)), Token);
        await b.Port.Enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Port.Id, Keithley2450Parameters.SourceVoltage.Id, new(2, PhysicalUnit.Volt), Token));
        b.Runs.Resume();
        if (maximum == 1) { await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Single(b.Port.EnabledAt); }
        else { await running; Assert.Equal(new[] { 5.0, 5.0 }, b.Port.EnabledAt); Assert.Equal(new(2, PhysicalUnit.Dimensionless), Assert.Single(b.Runs.LatestDataSet!.Rows).Values[new("attempts")]); Assert.Equal(1, b.Port.Frames); }
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("inter")]
    [InlineData("read")]
    public async Task PausingLaterStagesRestartsFromPolarizationVoltage(string stage)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var pulse = b.Pulse(inter: stage == "inter" ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero, read: stage == "read" ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero);
        var running = b.Runs.StartPlanAsync(b.Plan(pulse), Token); await (stage == "inter" ? b.Port.InterWritten.Task : b.Port.ReadWritten.Task).WaitAsync(TimeSpan.FromSeconds(5));
        b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); b.Runs.Resume(); await running;
        Assert.Equal(new[] { 5.0, 5.0 }, b.Port.EnabledAt); Assert.Equal(new(2, PhysicalUnit.Dimensionless), Assert.Single(b.Runs.LatestDataSet!.Rows).Values[pulse.AttemptsColumn]); Assert.Equal(1, b.Port.Frames);
    }
    [Fact]
    public async Task PausedDriftRejectsResumeWithoutReenablingOutput()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Pulse(pulse: TimeSpan.FromSeconds(30))), Token);
        await b.Port.Enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        var before = await b.Port.GetSnapshotAsync(Token); await b.Port.ConfigureSourceAsync(before.Source with { Level = 4 }, Token);
        b.Runs.Resume(); await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Single(b.Port.EnabledAt); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("mode")]
    [InlineData("scan")]
    public async Task InvalidPreparationAndEnclosingScanRejectBeforeConnection(string failure)
    {
        var b = new Bench(); var pulse = b.Pulse(); ExperimentPlan plan = b.Plan(pulse);
        if (failure == "missing") plan = new("Missing", pulse);
        if (failure == "mode") plan = new("Wrong", pulse, [new(b.Port.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        if (failure == "scan") plan = b.Plan(new ScanStep(new("scan"), new(b.Port.Id, Keithley2450Parameters.SourceVoltage.Id), new("axis"), new([new(1, PhysicalUnit.Volt)]), TimeSpan.Zero, pulse));
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
}

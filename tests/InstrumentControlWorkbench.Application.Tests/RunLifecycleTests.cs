using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class RunLifecycleTests
{
    private static readonly IvSweepRecipe Recipe = new("lifecycle", 0, 0.1, 3, 105e-6, 0);

    [Fact]
    public async Task ConcurrentAdmissionAndManualCallsCannotTouchOwnedHardware()
    {
        var (service, manager, driver, leases, hub) = Create();
        var running = service.StartIvSweepAsync(Recipe with { Points = 20, DwellSeconds = 10 }, CancellationToken.None);
        await ReadUntilAsync(hub, WorkbenchNotificationKind.MeasurementPointReceived);
        var rejected = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await Record.ExceptionAsync(() => service.StartIvSweepAsync(Recipe, CancellationToken.None))));
        Assert.All(rejected, e => Assert.IsType<InvalidOperationException>(e));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAsync(
            new(SourceFunction.Voltage, 0.2, 105e-6), new(SenseFunction.Current, SensingMode.FourWire, 1, true), CancellationToken.None));
        Assert.True(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow.AddYears(1)));
        service.Abort();
        await running;
        Assert.False(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow));
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }

    [Fact]
    public async Task ForeignLeaseRejectionDoesNotStopAnotherOwnersOutput()
    {
        var (service, _, driver, leases, _) = Create();
        await driver.ConnectAsync(CancellationToken.None);
        await driver.EnableOutputAsync(CancellationToken.None);
        leases.AcquireExclusive(driver.Id, "manual", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.True((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.Equal(0, driver.StopCalls);
        Assert.False(service.HasActiveRun);
        await driver.SafeStopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopFailureRetainsOwnershipAndRecoveryAllowsAnotherRun()
    {
        var (service, manager, driver, leases, hub) = Create();
        driver.StopError = new IOException("output command failed");
        await Assert.ThrowsAsync<IOException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.True(service.HasActiveRun);
        Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, service.Phase);
        Assert.True(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAsync(CancellationToken.None));
        Assert.Equal(MeasurementRunState.Faulted, service.LatestDataSet!.State);
        Assert.Contains(Drain(hub), n => n.Kind == WorkbenchNotificationKind.RunFaulted);
        driver.StopError = null;
        await service.RecoverSafetyAsync(CancellationToken.None);
        Assert.False(service.HasActiveRun);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        await service.StartIvSweepAsync(Recipe, CancellationToken.None);
        Assert.Equal(2, service.History.Runs.Count);
    }

    [Fact]
    public async Task OutputStillEnabledCannotBeReportedAsSafe()
    {
        var (service, _, driver, _, hub) = Create();
        driver.IgnoreStop = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.Contains("confirm output off", error.Message);
        Assert.True((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.DoesNotContain(Drain(hub), n => n.Kind == WorkbenchNotificationKind.SafetyStateChanged);
        driver.IgnoreStop = false;
        await service.RecoverSafetyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NonCooperativeStopIsBoundedAndRecoveryNeverOverlapsThePendingAttempt()
    {
        var (service, _, driver, leases, _) = Create(safetyTimeout: TimeSpan.FromMilliseconds(100));
        driver.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<TimeoutException>(() => service.RecoverSafetyAsync(CancellationToken.None));
        Assert.Equal(1, driver.StopCalls);
        driver.StopGate.SetResult();
        // The timed-out adapter deliberately ignored its token. Its snapshot query
        // observes the elapsed deadline, so explicit recovery must confirm again.
        await driver.StopReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        driver.StopGate = null;
        try { await service.RecoverSafetyAsync(CancellationToken.None); }
        catch (TimeoutException) { await service.RecoverSafetyAsync(CancellationToken.None); }
        Assert.False(service.HasActiveRun);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.Equal(2, driver.StopCalls);
    }

    [Fact]
    public async Task OriginalReadFaultAndCleanupFaultAreBothPreserved()
    {
        var (service, _, driver, _, _) = Create();
        driver.ReadError = new IOException("measurement transport lost");
        driver.StopError = new IOException("cleanup transport lost");
        var error = await Assert.ThrowsAsync<AggregateException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.Collection(error.InnerExceptions,
            e => Assert.Equal("measurement transport lost", e.Message),
            e => Assert.Equal("cleanup transport lost", e.Message));
        driver.ReadError = null;
        driver.StopError = null;
        await service.RecoverSafetyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MidRunReadFaultRetainsPartialResultsAndStopsBeforeFaultNotification()
    {
        var (service, _, driver, _, hub) = Create();
        driver.ReadError = new IOException("second read failed");
        driver.FailReadAt = 2;
        await Assert.ThrowsAsync<IOException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.Single(service.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Faulted, service.LatestDataSet.State);
        Assert.Single(service.History.Runs.Single().Rows);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        var events = Drain(hub);
        Assert.True(events.FindIndex(n => n.Kind == WorkbenchNotificationKind.SafetyStateChanged) <
            events.FindIndex(n => n.Kind == WorkbenchNotificationKind.RunFaulted));
        Assert.False(service.HasActiveRun);
    }

    [Fact]
    public async Task UnrequestedDriverCancellationIsAFault()
    {
        var (service, _, driver, _, hub) = Create();
        driver.ReadError = new OperationCanceledException("driver cancelled without operator request");
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.Equal(MeasurementRunState.Faulted, service.LatestOutcome!.State);
        Assert.Contains(Drain(hub), n => n.Kind == WorkbenchNotificationKind.RunFaulted);
        Assert.False(service.HasActiveRun);
    }

    [Fact]
    public async Task RunStaysActiveUntilResultFinalizationAndTerminalEventFollowsArchive()
    {
        var archive = new TestResultListener { BlockFinalization = true };
        var (service, _, driver, _, hub) = Create([archive]);
        var running = service.StartIvSweepAsync(Recipe, CancellationToken.None);
        await archive.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.HasActiveRun);
        Assert.Equal(RunExecutionPhase.Finalizing, service.Phase);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.DoesNotContain(Drain(hub), n => n.Kind == WorkbenchNotificationKind.RunCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        archive.Release.SetResult();
        await running;
        Assert.True(archive.Finalized);
        Assert.Contains(Drain(hub), n => n.Kind == WorkbenchNotificationKind.RunCompleted);
        Assert.False(service.HasActiveRun);
        Assert.Single(service.History.Runs);
    }

    [Fact]
    public async Task ArchiveFailureDoesNotSkipOtherListenersOrLoseAcquiredData()
    {
        var failing = new TestResultListener { FinalizationError = new IOException("archive full") };
        var succeeding = new TestResultListener();
        var (service, _, driver, leases, hub) = Create([failing, succeeding]);
        await Assert.ThrowsAsync<IOException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        Assert.True(succeeding.Finalized);
        Assert.Equal(3, succeeding.Rows);
        Assert.Equal(MeasurementRunState.Completed, service.LatestDataSet!.State);
        Assert.Equal(MeasurementRunState.Faulted, service.LatestOutcome!.State);
        Assert.Single(service.History.Runs);
        Assert.False(service.HasActiveRun);
        Assert.False(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow));
        Assert.Contains(Drain(hub), n => n.Kind == WorkbenchNotificationKind.RunFaulted);
    }

    [Fact]
    public async Task PauseConfirmsOutputOffKeepsResourcesAndResumeKeepsRunIdentity()
    {
        var (service, _, driver, leases, hub) = Create();
        var running = service.StartIvSweepAsync(Recipe with { DwellSeconds = 10 }, CancellationToken.None);
        await ReadUntilAsync(hub, WorkbenchNotificationKind.MeasurementPointReceived);
        service.Pause();
        var paused = await ReadUntilAsync(hub, WorkbenchNotificationKind.RunPaused);
        var count = service.LatestDataSet!.Rows.Count;
        Assert.Equal(RunExecutionPhase.Paused, service.Phase);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.True(leases.IsLeased(driver.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartIvSweepAsync(Recipe, CancellationToken.None));
        service.Resume();
        var resumed = await ReadUntilAsync(hub, WorkbenchNotificationKind.RunResumed);
        Assert.Equal(paused.RunId, resumed.RunId);
        service.Abort();
        await running;
        Assert.InRange(service.LatestDataSet.Rows.Count, 1, 2);
        Assert.True(count < 3);
        Assert.Equal(MeasurementRunState.Aborted, service.LatestOutcome!.State);
    }

    [Fact]
    public async Task AbortWhilePausedWakesTheGateAndFinalizesPartialData()
    {
        var (service, _, driver, _, hub) = Create();
        var running = service.StartIvSweepAsync(Recipe with { DwellSeconds = 0.1 }, CancellationToken.None);
        await ReadUntilAsync(hub, WorkbenchNotificationKind.MeasurementPointReceived);
        service.Pause();
        await ReadUntilAsync(hub, WorkbenchNotificationKind.RunPaused);
        service.Abort();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeasurementRunState.Aborted, service.LatestDataSet!.State);
        Assert.Single(service.LatestDataSet.Rows);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.False(service.HasActiveRun);
    }

    [Theory]
    [InlineData(211, 2, 0)]
    [InlineData(0.1, 1, 0)]
    [InlineData(0.1, 2, -1)]
    [InlineData(0.1, 2, double.NaN)]
    public async Task EntireRecipeIsCheckedBeforeConnecting(double stop, int points, double dwell)
    {
        var (service, _, driver, _, hub) = Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.StartIvSweepAsync(
            Recipe with { StopVoltage = stop, Points = points, DwellSeconds = dwell }, CancellationToken.None));
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).Connected);
        Assert.Empty(Drain(hub));
    }

    private static (MeasurementRunService, InstrumentManager, ControlledDriver, InstrumentLeaseService, NotificationHub)
        Create(IEnumerable<IRunResultListener>? listeners = null, TimeSpan? safetyTimeout = null)
    {
        var hub = new NotificationHub();
        var driver = new ControlledDriver();
        var manager = new InstrumentManager(driver, hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromMilliseconds(1));
        return (new(manager, leases, hub, listeners, safetyTimeout), manager, driver, leases, hub);
    }

    private static async Task<WorkbenchNotification> ReadUntilAsync(NotificationHub hub, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var notification = await hub.Reader.ReadAsync(deadline.Token);
            if (notification.Kind == kind) return notification;
        }
    }

    private static List<WorkbenchNotification> Drain(NotificationHub hub)
    {
        var result = new List<WorkbenchNotification>();
        while (hub.Reader.TryRead(out var notification)) result.Add(notification);
        return result;
    }

    private sealed class ControlledDriver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public Exception? ReadError { get; set; }
        public int FailReadAt { get; set; } = 1;
        private int readCalls;
        public Exception? StopError { get; set; }
        public bool IgnoreStop { get; set; }
        public TaskCompletionSource? StopGate { get; set; }
        public TaskCompletionSource StopReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls { get; private set; }
        public InstrumentId Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public Core.Devices.DeviceDescriptor Descriptor => inner.Descriptor;
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint setpoint, CancellationToken token) => inner.ConfigureSourceAsync(setpoint, token);
        public Task ConfigureSenseAsync(SenseConfiguration configuration, CancellationToken token) => inner.ConfigureSenseAsync(configuration, token);
        public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token);
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token)
        {
            readCalls++;
            return ReadError is { } error && readCalls >= FailReadAt
                ? Task.FromException<MeasurementValue>(error) : inner.ReadAsync(token);
        }
        public async Task SafeStopAsync(CancellationToken token)
        {
            StopCalls++;
            if (StopError is { } error) throw error;
            if (IgnoreStop) return;
            if (StopGate is { } pending)
            {
                await pending.Task;
                await inner.SafeStopAsync(CancellationToken.None);
                StopReturned.TrySetResult();
            }
            else await inner.SafeStopAsync(token);
        }
    }

    private sealed class TestResultListener : IRunResultListener
    {
        public bool BlockFinalization { get; init; }
        public Exception? FinalizationError { get; init; }
        public TaskCompletionSource Finalizing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finalized { get; private set; }
        public int Rows { get; private set; }
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Empty(dataSet.Rows);
            Assert.Equal(MeasurementRunState.Running, dataSet.State);
            return Task.CompletedTask;
        }
        public Task OnRowAsync(RunId runId, MeasurementDataRow row, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Rows++;
            return Task.CompletedTask;
        }
        public async Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token)
        {
            Finalizing.TrySetResult();
            if (BlockFinalization) await Release.Task.WaitAsync(token);
            if (FinalizationError is { } error) throw error;
            Assert.Equal(Rows, dataSet.Rows.Count);
            Finalized = true;
        }
    }
}

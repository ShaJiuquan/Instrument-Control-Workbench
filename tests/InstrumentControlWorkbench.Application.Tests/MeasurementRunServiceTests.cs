using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class MeasurementRunServiceTests
{
    [Fact]
    public async Task AbortDuringConnectCanBeRetried()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var service = new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        var recipe = new IvSweepRecipe("early stop", 0, 0.1, 2, 105e-6, 0.02);
        var running = service.StartIvSweepAsync(recipe, CancellationToken.None);
        service.Abort();
        await running;
        Assert.False(service.HasActiveRun);
        await service.StartIvSweepAsync(recipe, CancellationToken.None);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        var seen = new List<WorkbenchNotification>();
        while (hub.Reader.TryRead(out var n)) seen.Add(n);
        Assert.Contains(seen, n => n.Kind == WorkbenchNotificationKind.RunAborted);
        Assert.Contains(seen, n => n.Kind == WorkbenchNotificationKind.RunCompleted);
    }

    [Fact]
    public async Task DriverValidationFailureSafelyStopsAndAllowsRetry()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var service = new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.StartIvSweepAsync(
            new IvSweepRecipe("bad voltage", 211, 212, 2, 105e-6, 0.02), CancellationToken.None));
        Assert.False(service.HasActiveRun);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        var seen = new List<WorkbenchNotification>();
        while (hub.Reader.TryRead(out var n)) seen.Add(n);
        Assert.True(seen.FindIndex(n => n.Kind == WorkbenchNotificationKind.SafetyStateChanged) < seen.FindIndex(n => n.Kind == WorkbenchNotificationKind.RunFaulted));
        Assert.Contains(seen, n => n.Kind == WorkbenchNotificationKind.RunFaulted);
        await service.StartIvSweepAsync(new IvSweepRecipe("retry", 0, 0.1, 2, 105e-6, 0.02), CancellationToken.None);
    }

    [Fact]
    public async Task ConnectedBenchSupportsConsecutiveRuns()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var service = new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        await manager.ConnectAsync(CancellationToken.None);
        var recipe = new IvSweepRecipe("repeat", 0, 0.1, 2, 105e-6, 0.02);
        await service.StartIvSweepAsync(recipe, CancellationToken.None);
        await service.StartIvSweepAsync(recipe, CancellationToken.None);
        Assert.False(service.HasActiveRun);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        var completed = 0;
        while (hub.Reader.TryRead(out var n)) if (n.Kind == WorkbenchNotificationKind.RunCompleted) completed++;
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task PreCancelledRunReleasesLeaseAndCanBeRetried()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var service = new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var recipe = new IvSweepRecipe("cancel", 0, 0.1, 2, 105e-6, 0.02);
        await service.StartIvSweepAsync(recipe, cancelled.Token);
        Assert.False(service.HasActiveRun);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        await service.StartIvSweepAsync(recipe, CancellationToken.None);
        Assert.False(service.HasActiveRun);
    }

    [Fact]
    public async Task AbortRetainsPartialPointsAndConfirmsOutputOffBeforeTerminalEvent()
    {
        var hub = new NotificationHub();
        var driver = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(driver, hub);
        var service = new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        var running = service.StartIvSweepAsync(new IvSweepRecipe("abort", 0, 0.2, 21, 105e-6, 0.1), CancellationToken.None);
        var seen = new List<WorkbenchNotification>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var n = await hub.Reader.ReadAsync(timeout.Token);
            seen.Add(n);
            if (n.Kind == WorkbenchNotificationKind.MeasurementPointReceived) break;
        }
        service.Abort();
        await running;
        while (hub.Reader.TryRead(out var n)) seen.Add(n);
        Assert.False((await driver.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.Equal(1, seen.Count(n => n.Kind == WorkbenchNotificationKind.MeasurementPointReceived));
        Assert.True(seen.FindIndex(n => n.Kind == WorkbenchNotificationKind.SafetyStateChanged) < seen.FindIndex(n => n.Kind == WorkbenchNotificationKind.RunAborted));
        Assert.Contains(seen, n => n.Kind == WorkbenchNotificationKind.RunAborted);
    }

    [Fact]
    public async Task IvSweepPublishesPointsAndSafeCompletion()
    {
        var hub = new NotificationHub();
        var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var service = new MeasurementRunService(manager, leases, hub);
        var recipe = new IvSweepRecipe("test sweep", 0.0, 0.2, 3, 105e-6, 0.02);

        await service.StartIvSweepAsync(recipe, CancellationToken.None);

        var notifications = new List<WorkbenchNotification>();
        while (hub.Reader.TryRead(out var notification))
        {
            notifications.Add(notification);
        }

        Assert.Equal(3, notifications.Count(n => n.Kind == WorkbenchNotificationKind.MeasurementPointReceived));
        Assert.Contains(notifications, n => n.Kind == WorkbenchNotificationKind.RunCompleted);
        Assert.Contains(notifications, n => n.Kind == WorkbenchNotificationKind.SafetyStateChanged);
    }
}

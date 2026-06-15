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

using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class NotificationHubTests
{
    [Fact]
    public async Task IndependentConsumersReceiveTheSameOrderedFactsAndTimestamp()
    {
        var hub = new NotificationHub();
        using var second = hub.Subscribe();
        using var third = hub.Subscribe();
        foreach (var kind in new[] { WorkbenchNotificationKind.RunStarted, WorkbenchNotificationKind.RunFinalizing, WorkbenchNotificationKind.RunCompleted })
            await hub.PublishAsync(new(kind, kind.ToString()));
        foreach (var kind in new[] { WorkbenchNotificationKind.RunStarted, WorkbenchNotificationKind.RunFinalizing, WorkbenchNotificationKind.RunCompleted })
        {
            var firstEvent = await hub.Reader.ReadAsync();
            var secondEvent = await second.Reader.ReadAsync();
            var thirdEvent = await third.Reader.ReadAsync();
            Assert.Equal(kind, firstEvent.Kind);
            Assert.Same(firstEvent, secondEvent);
            Assert.Same(firstEvent, thirdEvent);
            Assert.NotNull(firstEvent.Timestamp);
            Assert.Equal(firstEvent.OccurredAt, secondEvent.OccurredAt);
        }
    }

    [Fact]
    public async Task DisposedSubscriptionDoesNotAffectOtherConsumersAndCancelledPublishAddsNothing()
    {
        var hub = new NotificationHub();
        var removed = hub.Subscribe();
        removed.Dispose();
        removed.Dispose();
        await removed.Reader.Completion;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => hub.PublishAsync(new(WorkbenchNotificationKind.RunStarted, "cancelled"), cancelled.Token).AsTask());
        Assert.False(hub.Reader.TryRead(out _));
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunCompleted, "ok"));
        Assert.Equal("ok", (await hub.Reader.ReadAsync()).Message);
        Assert.False(removed.Reader.TryRead(out _));
    }
}

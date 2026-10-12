using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class NotificationHubCapacityTests
{
    [Fact]
    public async Task ReliableOverflowFailsOnlyTheSlowObserverAfterItsAcceptedFactsDrain()
    {
        var hub = new NotificationHub(2); using var slow = hub.Subscribe(2); using var fast = hub.Subscribe(2);
        for (var i = 0; i < 10; i++)
        {
            await hub.PublishAsync(new(WorkbenchNotificationKind.LogAppended, i.ToString()));
            Assert.Equal(i.ToString(), (await fast.Reader.ReadAsync()).Message);
        }
        Assert.Equal(2, slow.Reader.Count); Assert.Equal(2, hub.Reader.Count);
        Assert.Equal("0", (await slow.Reader.ReadAsync()).Message); Assert.Equal("1", (await slow.Reader.ReadAsync()).Message);
        var failure = await Assert.ThrowsAsync<NotificationLagException>(() => slow.Reader.WaitToReadAsync().AsTask());
        Assert.Equal(2, failure.Capacity); await Assert.ThrowsAsync<NotificationLagException>(() => slow.Reader.Completion);
        while (hub.Reader.TryRead(out _)) { }
        await Assert.ThrowsAsync<NotificationLagException>(() => hub.Reader.Completion);
        await hub.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, "safe"));
        Assert.Equal("safe", (await fast.Reader.ReadAsync()).Message);
    }

    [Fact]
    public async Task DisplayOverflowEvictsOldTelemetryAndKeepsCriticalFactsInOrder()
    {
        var hub = new NotificationHub(); using var display = hub.SubscribeDisplay(4);
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunStarted, "started"));
        for (var i = 0; i < 20; i++) await hub.PublishAsync(new(WorkbenchNotificationKind.DataRowReceived, i.ToString(), RunId: "run", AcquiredRowCount: i + 1));
        await hub.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, "off confirmed"));
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunCompleted, "completed", RunId: "run", AcquiredRowCount: 20));
        var facts = Drain(display);
        Assert.Equal(new[] { "started", "19", "off confirmed", "completed" }, facts.Select(f => f.Message));
        Assert.Equal(19, display.OmittedDisplayFacts); Assert.Equal(20, facts[^1].AcquiredRowCount);
        Assert.False(display.Reader.Completion.IsCompleted);
    }

    [Theory]
    [InlineData(WorkbenchNotificationKind.MeasurementPointReceived)]
    [InlineData(WorkbenchNotificationKind.DataRowReceived)]
    [InlineData(WorkbenchNotificationKind.LogAppended)]
    [InlineData(WorkbenchNotificationKind.StepStarted)]
    [InlineData(WorkbenchNotificationKind.StepCompleted)]
    public async Task DisplayFactsCannotDisplaceAQueueContainingOnlySafetyAndLifecycle(WorkbenchNotificationKind kind)
    {
        var hub = new NotificationHub(); using var display = hub.SubscribeDisplay(2);
        await hub.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, "safety"));
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunFinalizing, "finalizing"));
        await hub.PublishAsync(new(kind, "display"));
        Assert.Equal(new[] { "safety", "finalizing" }, Drain(display).Select(f => f.Message)); Assert.Equal(1, display.OmittedDisplayFacts);
    }

    [Fact]
    public async Task CriticalOnlyOverflowReportsAGapInsteadOfSilentlyLosingAStateTransition()
    {
        var hub = new NotificationHub(); using var display = hub.SubscribeDisplay(2);
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunPaused, "paused"));
        await hub.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, "device"));
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunFaulted, "faulted"));
        Assert.Equal(new[] { "paused", "device" }, Drain(display).Select(f => f.Message));
        await Assert.ThrowsAsync<NotificationLagException>(() => display.Reader.WaitToReadAsync().AsTask());
        await Assert.ThrowsAsync<NotificationLagException>(() => display.Reader.Completion); Assert.Equal(0, display.OmittedDisplayFacts);
    }

    [Fact]
    public async Task CancelledWaitDoesNotConsumeALaterFactAndDisposeWakesOtherWaiters()
    {
        var hub = new NotificationHub(); var subscription = hub.Subscribe(); using var cancelled = new CancellationTokenSource();
        var waiting = subscription.Reader.WaitToReadAsync(cancelled.Token).AsTask(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunStarted, "next")); Assert.Equal("next", (await subscription.Reader.ReadAsync()).Message);
        var end = subscription.Reader.WaitToReadAsync().AsTask(); subscription.Dispose(); Assert.False(await end.WaitAsync(TimeSpan.FromSeconds(2))); await subscription.Reader.Completion;
    }

    [Fact]
    public async Task ConcurrentPublishReadAndDisposeRespectTheBoundAndDoNotBlockOtherConsumers()
    {
        var hub = new NotificationHub(); using var display = hub.SubscribeDisplay(8); var removed = hub.SubscribeDisplay(8);
        var publisher = Task.Run(async () => { for (var i = 0; i < 10_000; i++) { await hub.PublishAsync(new(WorkbenchNotificationKind.LogAppended, i.ToString())); Assert.InRange(display.Reader.Count, 0, 8); } });
        var reader = Task.Run(async () => { while (!publisher.IsCompleted) { while (display.Reader.TryRead(out _)) { } await Task.Yield(); } });
        removed.Dispose(); await Task.WhenAll(publisher, reader).WaitAsync(TimeSpan.FromSeconds(5));
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunCompleted, "terminal"));
        Assert.Contains(Drain(display), fact => fact.Message == "terminal");
        while (hub.Reader.TryRead(out _)) { }
        await Assert.ThrowsAsync<NotificationLagException>(() => hub.Reader.Completion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public void InvalidCapacityIsRejected(int capacity)
    { Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationHub(capacity)); var hub = new NotificationHub(); Assert.Throws<ArgumentOutOfRangeException>(() => hub.SubscribeDisplay(capacity)); }

    private static List<WorkbenchNotification> Drain(NotificationSubscription subscription)
    { var facts = new List<WorkbenchNotification>(); while (subscription.Reader.TryRead(out var fact)) facts.Add(fact); return facts; }
}

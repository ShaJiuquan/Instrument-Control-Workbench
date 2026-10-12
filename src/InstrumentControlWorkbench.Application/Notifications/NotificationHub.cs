using System.Threading.Channels;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Notifications;

public sealed class NotificationHub
{
    private readonly object gate = new();
    private readonly HashSet<NotificationMailbox> mailboxes = [];
    private readonly NotificationSubscription defaultSubscription;

    // Preserve the legacy Reader's from-construction observation, with bounded retention.
    public NotificationHub(int capacity = 4096) => defaultSubscription = Subscribe(capacity);
    public ChannelReader<WorkbenchNotification> Reader => defaultSubscription.Reader;
    public NotificationSubscription Subscribe(int capacity = 4096) => AddSubscription(capacity, false);
    public NotificationSubscription SubscribeDisplay(int capacity = 512) => AddSubscription(capacity, true);

    private NotificationSubscription AddSubscription(int capacity, bool allowDisplayGaps)
    {
        if (capacity is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        var mailbox = new NotificationMailbox(capacity, allowDisplayGaps);
        lock (gate) mailboxes.Add(mailbox);
        return new(mailbox, () =>
        {
            lock (gate) { mailboxes.Remove(mailbox); mailbox.Close(); }
        });
    }

    public ValueTask PublishAsync(WorkbenchNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        notification = notification with { Timestamp = notification.Timestamp ?? DateTimeOffset.UtcNow };
        lock (gate) mailboxes.RemoveWhere(mailbox => !mailbox.Publish(notification));
        return ValueTask.CompletedTask;
    }
}

public sealed class NotificationSubscription : IDisposable
{
    private readonly NotificationMailbox mailbox;
    private Action? unsubscribe;
    internal NotificationSubscription(NotificationMailbox mailbox, Action unsubscribe)
    { this.mailbox = mailbox; Reader = mailbox; this.unsubscribe = unsubscribe; }
    public ChannelReader<WorkbenchNotification> Reader { get; }
    public long OmittedDisplayFacts => mailbox.OmittedCount;
    public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
}

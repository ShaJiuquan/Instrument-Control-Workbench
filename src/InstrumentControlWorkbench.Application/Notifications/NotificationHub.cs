using System.Threading.Channels;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Notifications;

public sealed class NotificationHub
{
    private readonly object gate = new();
    private readonly HashSet<Channel<WorkbenchNotification>> channels = [];
    private readonly NotificationSubscription defaultSubscription;

    public NotificationHub() => defaultSubscription = Subscribe();

    public ChannelReader<WorkbenchNotification> Reader => defaultSubscription.Reader;

    public NotificationSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<WorkbenchNotification>(new() { SingleReader = true });
        lock (gate) channels.Add(channel);
        return new(channel.Reader, () =>
        {
            lock (gate)
            {
                channels.Remove(channel);
                channel.Writer.TryComplete();
            }
        });
    }

    public ValueTask PublishAsync(WorkbenchNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        notification = notification with { Timestamp = notification.Timestamp ?? DateTimeOffset.UtcNow };
        lock (gate)
            foreach (var channel in channels) channel.Writer.TryWrite(notification);
        return ValueTask.CompletedTask;
    }
}

public sealed class NotificationSubscription : IDisposable
{
    private Action? unsubscribe;

    internal NotificationSubscription(ChannelReader<WorkbenchNotification> reader, Action unsubscribe)
    {
        Reader = reader;
        this.unsubscribe = unsubscribe;
    }

    public ChannelReader<WorkbenchNotification> Reader { get; }
    public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
}

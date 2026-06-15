using System.Threading.Channels;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Notifications;

public sealed class NotificationHub
{
    private readonly Channel<WorkbenchNotification> channel = Channel.CreateUnbounded<WorkbenchNotification>();

    public ChannelReader<WorkbenchNotification> Reader => channel.Reader;

    public ValueTask PublishAsync(WorkbenchNotification notification, CancellationToken cancellationToken = default)
    {
        return channel.Writer.WriteAsync(notification, cancellationToken);
    }
}

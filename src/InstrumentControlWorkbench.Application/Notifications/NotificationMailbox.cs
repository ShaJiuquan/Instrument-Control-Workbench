using System.Threading.Channels;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Notifications;

// A bounded observer mailbox. Publishers never wait for an observer or the UI.
internal sealed class NotificationMailbox(int capacity, bool allowDisplayGaps) : ChannelReader<WorkbenchNotification>
{
    private readonly object gate = new();
    private readonly LinkedList<WorkbenchNotification> pending = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource changed = NewSignal();
    private bool closed;
    private Exception? error;
    private long omitted;

    public override Task Completion => completion.Task;
    public override bool CanCount => true;
    public override int Count { get { lock (gate) return pending.Count; } }
    public long OmittedCount { get { lock (gate) return omitted; } }

    public bool Publish(WorkbenchNotification fact)
    {
        lock (gate)
        {
            if (closed) return false;
            if (pending.Count == capacity)
            {
                var expendable = allowDisplayGaps ? pending.First : null;
                while (expendable is not null && !IsDisplayFact(expendable.Value.Kind)) expendable = expendable.Next;
                if (expendable is not null) { pending.Remove(expendable); omitted++; }
                else if (allowDisplayGaps && IsDisplayFact(fact.Kind)) { omitted++; return true; }
                else { Close(new NotificationLagException(capacity)); return false; }
            }
            pending.AddLast(fact);
            Signal();
            return true;
        }
    }

    public void Close(Exception? reason = null)
    {
        lock (gate)
        {
            if (closed) return;
            closed = true; error = reason;
            Signal(); CompleteIfDrained();
        }
    }

    public override bool TryRead(out WorkbenchNotification item)
    {
        lock (gate)
        {
            if (pending.First is not { } first) { item = null!; return false; }
            item = first.Value; pending.RemoveFirst(); CompleteIfDrained(); return true;
        }
    }

    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wake;
            lock (gate)
            {
                if (pending.Count != 0) return true;
                if (closed)
                {
                    if (error is not null) throw error;
                    return false;
                }
                wake = changed.Task;
            }
            await wake.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteIfDrained()
    {
        if (!closed || pending.Count != 0) return;
        if (error is null) completion.TrySetResult();
        else completion.TrySetException(error);
    }
    private void Signal() { var previous = changed; changed = NewSignal(); previous.TrySetResult(); }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsDisplayFact(WorkbenchNotificationKind kind) => kind is
        WorkbenchNotificationKind.MeasurementPointReceived or WorkbenchNotificationKind.DataRowReceived or
        WorkbenchNotificationKind.LogAppended or WorkbenchNotificationKind.StepStarted or WorkbenchNotificationKind.StepCompleted;
}

public sealed class NotificationLagException(int capacity) : Exception($"Notification observer exceeded its {capacity}-fact capacity; state must be refreshed before relying on this observer.")
{
    public int Capacity { get; } = capacity;
}

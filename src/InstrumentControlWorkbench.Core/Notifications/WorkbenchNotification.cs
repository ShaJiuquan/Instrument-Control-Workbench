using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Core.Notifications;

public enum WorkbenchNotificationKind
{
    InstrumentStateChanged,
    MeasurementPointReceived,
    RunStarted,
    RunCompleted,
    RunAborted,
    RunFaulted,
    SafetyStateChanged,
    LogAppended,
}

public sealed record WorkbenchNotification(
    WorkbenchNotificationKind Kind,
    string Message,
    InstrumentId? InstrumentId = null,
    SourceMeterSnapshot? Snapshot = null,
    MeasurementPoint? Point = null,
    string? RunId = null,
    DateTimeOffset? Timestamp = null)
{
    public DateTimeOffset OccurredAt => Timestamp ?? DateTimeOffset.Now;
}

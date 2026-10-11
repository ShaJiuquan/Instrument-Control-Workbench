using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record CurrentResistanceRetryEvidence
{
    public CurrentResistanceRetryEvidence(InstrumentReadFailureKind kind, int? commandCode, string message,
        DateTimeOffset attemptStartedAt, DateTimeOffset failedAt, DateTimeOffset idleConfirmedAt, DateTimeOffset? sourceOffConfirmedAt, Keithley2182Settings meterSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message); ArgumentNullException.ThrowIfNull(meterSettings);
        if (message.Length > 4096 || (kind == InstrumentReadFailureKind.CommandRejected) != (commandCode is not null) || !InstrumentReadException.IsSingleRetryEligible(kind, commandCode) || attemptStartedAt > failedAt || failedAt > idleConfirmedAt ||
            sourceOffConfirmedAt is { } off && (off < failedAt || off > idleConfirmedAt))
            throw new ArgumentException("Retry evidence needs an eligible failure and ordered fresh idle/output-off confirmation.");
        MeterSettings = meterSettings; Kind = kind; CommandCode = commandCode; Message = message; AttemptStartedAt = attemptStartedAt;
        FailedAt = failedAt; IdleConfirmedAt = idleConfirmedAt; SourceOffConfirmedAt = sourceOffConfirmedAt;
    }
    public Keithley2182Settings MeterSettings { get; }
    public InstrumentReadFailureKind Kind { get; }
    public int? CommandCode { get; }
    public string Message { get; }
    public DateTimeOffset AttemptStartedAt { get; }
    public DateTimeOffset FailedAt { get; }
    public DateTimeOffset IdleConfirmedAt { get; }
    public DateTimeOffset? SourceOffConfirmedAt { get; }
    public void Validate(CurrentResistanceRetry? policy, DateTimeOffset sourceConfirmedBefore)
    {
        if (policy is null || policy.OwnsSourceOutput != (SourceOffConfirmedAt is not null) || IdleConfirmedAt > sourceConfirmedBefore)
            throw new ArgumentException("Retry proof differs from the frozen acquisition policy or successful attempt interval.");
    }
}

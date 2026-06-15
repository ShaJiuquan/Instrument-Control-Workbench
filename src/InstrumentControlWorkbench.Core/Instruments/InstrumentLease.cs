namespace InstrumentControlWorkbench.Core.Instruments;

public enum InstrumentLeaseMode
{
    Exclusive,
    SharedRead,
}

public sealed record InstrumentLease(
    InstrumentId InstrumentId,
    string OwnerRunId,
    InstrumentLeaseMode Mode,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

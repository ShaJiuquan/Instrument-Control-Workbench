namespace InstrumentControlWorkbench.Core.Instruments;

public enum InstrumentLeaseMode
{
    Exclusive,
    SharedRead,
}

public enum InstrumentLeaseLifetime { SessionTtl, RunOwned }

public sealed record InstrumentLease(
    InstrumentId InstrumentId,
    string OwnerRunId,
    InstrumentLeaseMode Mode,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    InstrumentLeaseLifetime Lifetime = InstrumentLeaseLifetime.SessionTtl)
{
    public bool IsExpired(DateTimeOffset now) => Lifetime == InstrumentLeaseLifetime.SessionTtl && now >= ExpiresAt;
}

using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Instruments;

public sealed class InstrumentLeaseService
{
    private readonly TimeSpan defaultTtl;
    private readonly Dictionary<InstrumentId, InstrumentLease> leases = new();
    private readonly object gate = new();

    public InstrumentLeaseService(TimeSpan defaultTtl)
    {
        this.defaultTtl = defaultTtl;
    }

    public InstrumentLease AcquireExclusive(InstrumentId instrumentId, string ownerRunId, DateTimeOffset now)
    {
        lock (gate)
        {
            RemoveExpired(now);

            if (leases.TryGetValue(instrumentId, out var existing))
            {
                throw new InvalidOperationException(
                    $"{instrumentId} is already leased by {existing.OwnerRunId} until {existing.ExpiresAt:O}.");
            }

            var lease = new InstrumentLease(
                instrumentId,
                ownerRunId,
                InstrumentLeaseMode.Exclusive,
                now,
                now.Add(defaultTtl));
            leases[instrumentId] = lease;
            return lease;
        }
    }

    public InstrumentLease Refresh(InstrumentId instrumentId, string ownerRunId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(instrumentId, out var existing) || existing.OwnerRunId != ownerRunId)
            {
                throw new InvalidOperationException($"{ownerRunId} does not own {instrumentId}.");
            }

            var refreshed = existing with { ExpiresAt = now.Add(defaultTtl) };
            leases[instrumentId] = refreshed;
            return refreshed;
        }
    }

    public void Release(InstrumentId instrumentId, string ownerRunId)
    {
        lock (gate)
        {
            if (leases.TryGetValue(instrumentId, out var existing) && existing.OwnerRunId == ownerRunId)
            {
                leases.Remove(instrumentId);
            }
        }
    }

    public bool IsLeased(InstrumentId instrumentId, DateTimeOffset now)
    {
        lock (gate)
        {
            RemoveExpired(now);
            return leases.ContainsKey(instrumentId);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var expired in leases.Where(pair => pair.Value.IsExpired(now)).Select(pair => pair.Key).ToArray())
        {
            leases.Remove(expired);
        }
    }
}

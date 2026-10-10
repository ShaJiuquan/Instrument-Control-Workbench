using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Instruments;

public sealed class InstrumentLeaseService
{
    private readonly TimeSpan defaultTtl;
    private readonly Dictionary<InstrumentId, InstrumentLease> leases = new();
    private readonly object gate = new();

    public InstrumentLeaseService(TimeSpan defaultTtl)
    {
        if (defaultTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(defaultTtl));
        this.defaultTtl = defaultTtl;
    }

    public InstrumentLease AcquireRunExclusive(InstrumentId instrumentId, RunId owner, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId.Value);
        ArgumentNullException.ThrowIfNull(owner);
        lock (gate)
        {
            RemoveExpired(now);
            if (leases.ContainsKey(instrumentId)) throw new InvalidOperationException($"{instrumentId} is already leased.");
            var lease = new InstrumentLease(instrumentId, owner.ToString(), InstrumentLeaseMode.Exclusive,
                now, DateTimeOffset.MaxValue, InstrumentLeaseLifetime.RunOwned);
            leases.Add(instrumentId, lease);
            return lease;
        }
    }

    public IReadOnlyList<InstrumentLease> AcquireRunExclusive(IEnumerable<InstrumentId> instrumentIds, RunId owner, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instrumentIds);
        ArgumentNullException.ThrowIfNull(owner);
        var ids = instrumentIds.Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("A resource group must not be empty.", nameof(instrumentIds));
        foreach (var id in ids) ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        lock (gate)
        {
            RemoveExpired(now);
            foreach (var id in ids)
                if (leases.ContainsKey(id)) throw new InvalidOperationException($"{id} is already leased.");
            var acquired = ids.Select(id => new InstrumentLease(id, owner.ToString(), InstrumentLeaseMode.Exclusive,
                now, DateTimeOffset.MaxValue, InstrumentLeaseLifetime.RunOwned)).ToArray();
            foreach (var lease in acquired) leases.Add(lease.InstrumentId, lease);
            return Array.AsReadOnly(acquired);
        }
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
            RemoveExpired(now);
            if (!leases.TryGetValue(instrumentId, out var existing) || existing.OwnerRunId != ownerRunId)
            {
                throw new InvalidOperationException($"{ownerRunId} does not own {instrumentId}.");
            }

            var refreshed = existing.Lifetime == InstrumentLeaseLifetime.RunOwned ? existing : existing with { ExpiresAt = now.Add(defaultTtl) };
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

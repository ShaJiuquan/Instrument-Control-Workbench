using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class InstrumentLeaseServiceTests
{
    [Fact]
    public void RunOwnershipSurvivesTtlAndCanOnlyBeReleasedByItsOwner()
    {
        var leases = new InstrumentLeaseService(TimeSpan.FromMilliseconds(1));
        var instrument = new InstrumentId("k2450");
        var owner = Core.Data.RunId.New();
        var now = DateTimeOffset.UnixEpoch;
        leases.AcquireRunExclusive(instrument, owner, now);
        leases.Release(instrument, "another run");
        Assert.True(leases.IsLeased(instrument, now.AddYears(1)));
        Assert.Throws<InvalidOperationException>(() => leases.AcquireExclusive(instrument, "other", now.AddYears(1)));
        leases.Refresh(instrument, owner.ToString(), now.AddYears(1));
        leases.Release(instrument, owner.ToString());
        Assert.False(leases.IsLeased(instrument, now.AddYears(1)));
    }

    [Fact]
    public void ExpiredSessionCannotRefreshItsFormerLease()
    {
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(1));
        var instrument = new InstrumentId("k2450");
        var now = DateTimeOffset.UnixEpoch;
        leases.AcquireExclusive(instrument, "owner", now);
        Assert.Throws<InvalidOperationException>(() => leases.Refresh(instrument, "owner", now.AddSeconds(2)));
        Assert.False(leases.IsLeased(instrument, now.AddSeconds(2)));
    }

    [Fact]
    public void ExclusiveLeaseRejectsConcurrentOwnerUntilTtlExpires()
    {
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(1));
        var instrument = new InstrumentId("k2450");
        var now = DateTimeOffset.UnixEpoch;

        leases.AcquireExclusive(instrument, "run-1", now);

        Assert.Throws<InvalidOperationException>(() =>
            leases.AcquireExclusive(instrument, "run-2", now.AddMilliseconds(100)));

        leases.AcquireExclusive(instrument, "run-2", now.AddSeconds(2));
        Assert.True(leases.IsLeased(instrument, now.AddSeconds(2)));
    }
}

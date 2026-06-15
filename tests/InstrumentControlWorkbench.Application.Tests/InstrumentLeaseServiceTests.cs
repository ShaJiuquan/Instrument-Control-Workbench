using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class InstrumentLeaseServiceTests
{
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

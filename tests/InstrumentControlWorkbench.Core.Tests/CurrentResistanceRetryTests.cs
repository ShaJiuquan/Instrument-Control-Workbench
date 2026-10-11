using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class CurrentResistanceRetryTests
{
    private static readonly Keithley2182Settings Meter = new(1, null, 1, false, 10, AveragingMode.Repeat, 1);
    [Theory]
    [InlineData(InstrumentReadFailureKind.ReadTimeout, null, true)]
    [InlineData(InstrumentReadFailureKind.BufferTimeout, null, true)]
    [InlineData(InstrumentReadFailureKind.CommandRejected, -221, true)]
    [InlineData(InstrumentReadFailureKind.CommandRejected, -241, false)]
    [InlineData(InstrumentReadFailureKind.CommandRejected, 419, false)]
    [InlineData(InstrumentReadFailureKind.Overrange, null, false)]
    [InlineData(InstrumentReadFailureKind.InvalidResponse, null, false)]
    [InlineData(InstrumentReadFailureKind.VisaIoRead, null, false)]
    public void OnlyExactTypedTransientAcquisitionFailuresAllowRetry(InstrumentReadFailureKind kind, int? code, bool expected)
        => Assert.Equal(expected, new InstrumentReadException(new("meter"), kind, "Read failed", code).AllowsSingleRetry);

    [Fact]
    public void InvalidPolicyAndFailureShapesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CurrentResistanceRetry(false, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrentResistanceRetry(true, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => new InstrumentReadException(new("meter"), InstrumentReadFailureKind.ReadTimeout, "Timeout", -221));
        Assert.Throws<ArgumentException>(() => new InstrumentReadException(new("meter"), InstrumentReadFailureKind.CommandRejected, "Rejected"));
        Assert.Throws<ArgumentException>(() => new InstrumentReadException(new("meter"), (InstrumentReadFailureKind)100, "Unknown"));
    }

    [Fact]
    public void RetryProofRequiresEligibleErrorAndOrderedActualConfirmation()
    {
        var t = DateTimeOffset.UtcNow;
        var retry = new CurrentResistanceRetryEvidence(InstrumentReadFailureKind.ReadTimeout, null, "Timed out", t, t.AddSeconds(1), t.AddSeconds(3), t.AddSeconds(2), Meter);
        retry.Validate(new(true, TimeSpan.Zero), t.AddSeconds(4));
        Assert.Throws<ArgumentException>(() => retry.Validate(null, t.AddSeconds(4)));
        Assert.Throws<ArgumentException>(() => retry.Validate(new(false, TimeSpan.Zero), t.AddSeconds(4)));
        Assert.Throws<ArgumentException>(() => retry.Validate(new(true, TimeSpan.Zero), t.AddSeconds(2)));
        Assert.Throws<ArgumentException>(() => new CurrentResistanceRetryEvidence(InstrumentReadFailureKind.ReadTimeout, null, "Timed out", t, t.AddSeconds(-1), t.AddSeconds(2), null, Meter));
        Assert.Throws<ArgumentException>(() => new CurrentResistanceRetryEvidence(InstrumentReadFailureKind.ReadTimeout, null, "Timed out", t, t.AddSeconds(1), t.AddSeconds(2), t.AddSeconds(3), Meter));
        Assert.Throws<ArgumentException>(() => new CurrentResistanceRetryEvidence(InstrumentReadFailureKind.Overrange, null, "Overrange", t, t.AddSeconds(1), t.AddSeconds(2), null, Meter));
    }
}

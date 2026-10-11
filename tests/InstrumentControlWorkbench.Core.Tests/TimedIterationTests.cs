using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class TimedIterationTests
{
    [Theory]
    [InlineData(-0.001, PhysicalUnit.Second)]
    [InlineData(4294967.295, PhysicalUnit.Second)]
    [InlineData(0, PhysicalUnit.Dimensionless)]
    [InlineData(1, PhysicalUnit.Volt)]
    public void InvalidTimePointsAreRejectedBeforeAdmission(double value, PhysicalUnit unit)
        => Assert.ThrowsAny<ArgumentException>(() => new ForEachStep(new("time"), new("point"), new([new(value, unit)]),
            new DelayStep(new("body"), TimeSpan.Zero), IterationTiming.DelayBeforePoint));

    [Fact]
    public void DefaultIteratorRetainsArbitraryUnitsAndNoWaiting()
    {
        var loop = new ForEachStep(new("loop"), new("value"), new([new(-1, PhysicalUnit.Volt)]), new DelayStep(new("body"), TimeSpan.Zero));
        Assert.Equal(IterationTiming.Immediate, loop.Timing);
        Assert.Equal(-1, loop.Values.Values.Single().Value);
    }

    [Fact]
    public void ZeroAndMaximumSupportedDelayAreAcceptedAndFrozen()
    {
        var input = new List<Quantity> { new(0, PhysicalUnit.Second), new((uint.MaxValue - 1) / 1000.0, PhysicalUnit.Second) };
        var loop = new ForEachStep(new("time"), new("point"), new(input), new DelayStep(new("body"), TimeSpan.Zero), IterationTiming.DelayBeforePoint);
        input.Clear(); Assert.Equal(2, loop.Values.Values.Count); Assert.Equal(IterationTiming.DelayBeforePoint, loop.Timing);
    }

    [Fact]
    public void UnknownTimingCannotFallBackToImmediateExecution()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ForEachStep(new("time"), new("point"), new([new(0, PhysicalUnit.Second)]),
            new DelayStep(new("body"), TimeSpan.Zero), (IterationTiming)42));
}

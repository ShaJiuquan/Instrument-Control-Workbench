using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class WaitFieldStepTests
{
    [Fact]
    public void FieldWaitHasLiveTargetAndNoHiddenDeadline()
    {
        var wait = new WaitFieldStep(new("wait"), new("field"), new(0, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Equal(PhysicalUnit.Tesla, wait.Tolerance.Unit); Assert.Equal(TimeSpan.Zero, wait.StableFor);
    }
    [Theory]
    [InlineData("unit")]
    [InlineData("negative_tolerance")]
    [InlineData("negative_duration")]
    [InlineData("zero_poll")]
    [InlineData("negative_poll")]
    [InlineData("timer_bound")]
    public void InvalidPhysicalAndTimerContractsAreRejected(string fault)
    {
        var tolerance = new Quantity(fault == "negative_tolerance" ? -1 : 0.1, fault == "unit" ? PhysicalUnit.Degree : PhysicalUnit.Tesla);
        var stable = fault == "negative_duration" ? TimeSpan.FromMilliseconds(-1) : TimeSpan.Zero;
        var poll = fault switch { "zero_poll" => TimeSpan.Zero, "negative_poll" => TimeSpan.FromMilliseconds(-1), "timer_bound" => TimeSpan.FromDays(100), _ => TimeSpan.FromMilliseconds(100) };
        Assert.ThrowsAny<ArgumentException>(() => new WaitFieldStep(new("wait"), new("field"), tolerance, stable, poll));
    }
}

using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ControllerWaitStepTests
{
    [Fact]
    public void LiveStateWaitsHaveNoHiddenDeadlineOrTargetWrite()
    {
        var angle = new WaitAngleStep(new("angle"), new("rotator"), new(0, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(1));
        Assert.Null(angle.Target); Assert.Equal(PhysicalUnit.Degree, angle.Tolerance.Unit);
        var temp = new WaitTemperatureStateStep(new("temp"), new("thermal"), TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(500));
        Assert.Equal(TemperatureWaitMode.Stable, temp.Mode);
    }
    [Theory]
    [InlineData("negative_tolerance")]
    [InlineData("wrong_tolerance")]
    [InlineData("wrong_target")]
    [InlineData("zero_poll")]
    [InlineData("negative_poll")]
    [InlineData("timer_bound")]
    public void AngleWaitRejectsInvalidUnitAndTimerContracts(string fault)
    {
        var tolerance = new Quantity(fault == "negative_tolerance" ? -1 : 0.1, fault == "wrong_tolerance" ? PhysicalUnit.Kelvin : PhysicalUnit.Degree);
        var poll = fault switch { "zero_poll" => TimeSpan.Zero, "negative_poll" => TimeSpan.FromMilliseconds(-1), "timer_bound" => TimeSpan.FromDays(100), _ => TimeSpan.FromMilliseconds(100) };
        Assert.ThrowsAny<ArgumentException>(() => new WaitAngleStep(new("wait"), new("rotator"), tolerance, poll,
            fault == "wrong_target" ? new ConstantValue(new(1, PhysicalUnit.Tesla)) : null));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TemperatureStateWaitRejectsNonpositivePoll(int milliseconds)
    { Assert.ThrowsAny<ArgumentException>(() => new WaitTemperatureStateStep(new("wait"), new("thermal"), TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(milliseconds))); }
    [Fact]
    public void TemperatureStateModeIsClosed()
    { Assert.Throws<ArgumentOutOfRangeException>(() => new WaitTemperatureStateStep(new("wait"), new("thermal"), (TemperatureWaitMode)99, TimeSpan.FromMilliseconds(100))); }
}

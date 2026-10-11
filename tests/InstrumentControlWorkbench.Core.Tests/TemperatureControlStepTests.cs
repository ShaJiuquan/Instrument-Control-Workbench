using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class TemperatureControlStepTests
{
    [Theory]
    [InlineData(TemperatureControlAction.RampOn, null)]
    [InlineData(TemperatureControlAction.RampOff, 5d)]
    [InlineData(TemperatureControlAction.ControlOn, 5d)]
    [InlineData(TemperatureControlAction.ControlOff, 5d)]
    public void RatesAreRequiredOnlyForRampOn(TemperatureControlAction action, double? rate)
        => Assert.Throws<ArgumentException>(() => new TemperatureControlStep(new("command"), new("temperature"), action,
            rate is { } value ? new(value, PhysicalUnit.KelvinPerMinute) : null));

    [Theory]
    [InlineData(0)]
    [InlineData(0.009)]
    [InlineData(20.001)]
    public void InvalidRampRateRejects(double rate)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureControlStep(new("command"), new("temperature"), TemperatureControlAction.RampOn, new(rate, PhysicalUnit.KelvinPerMinute)));

    [Fact]
    public void UndefinedActionAndWrongRateUnitReject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureControlStep(new("command"), new("temperature"), (TemperatureControlAction)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureControlStep(new("command"), new("temperature"), TemperatureControlAction.RampOn, new(5, PhysicalUnit.Kelvin)));
    }
}

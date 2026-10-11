using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class HardwareTemperatureSetpointTests
{
    [Theory]
    [InlineData(0.9, 7, PhysicalUnit.Kelvin, PhysicalUnit.KelvinPerMinute)]
    [InlineData(310.1, 7, PhysicalUnit.Kelvin, PhysicalUnit.KelvinPerMinute)]
    [InlineData(300, 0.001, PhysicalUnit.Kelvin, PhysicalUnit.KelvinPerMinute)]
    [InlineData(300, 21, PhysicalUnit.Kelvin, PhysicalUnit.KelvinPerMinute)]
    [InlineData(300, 7, PhysicalUnit.Tesla, PhysicalUnit.KelvinPerMinute)]
    [InlineData(300, 7, PhysicalUnit.Kelvin, PhysicalUnit.TeslaPerSecond)]
    public void HardwareSetpointRejectsUnsafeTargetRateAndUnits(double target, double rate, PhysicalUnit targetUnit, PhysicalUnit rateUnit)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HardwareTemperatureSetpointStep(new("hardware"), new("temperature"), new ConstantValue(new(target, targetUnit)), new(rate, rateUnit), enableControl: false));
    }
}

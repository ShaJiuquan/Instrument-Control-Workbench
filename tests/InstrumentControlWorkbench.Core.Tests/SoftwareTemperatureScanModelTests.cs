using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SoftwareTemperatureScanModelTests
{
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    [Theory]
    [InlineData(4, 5, 0, 4)]
    [InlineData(4, 5, .006, 4.001)]
    [InlineData(4, 5, 3, 4.5)]
    [InlineData(4, 5, 6, 5)]
    [InlineData(4, 5, 60, 5)]
    [InlineData(5, 4, .006, 4.999)]
    [InlineData(5, 4, 3, 4.5)]
    [InlineData(5, 4, 60, 4)]
    public void SubsecondTrajectoryRoundsToMilliKelvinAndClampsInBothDirections(double first, double last, double seconds, double expected)
        => Assert.Equal(K(expected), new SoftwareTemperatureTrajectory(K(first), K(last), new(10, PhysicalUnit.KelvinPerMinute)).At(TimeSpan.FromSeconds(seconds)));
    [Theory]
    [InlineData(4.0005, 4.001)]
    [InlineData(4.0025, 4.003)]
    [InlineData(4.0004, 4)]
    public void HalfMilliKelvinUsesAwayFromZero(double input, double expected) => Assert.Equal(K(expected), SoftwareTemperatureTrajectory.Round(K(input)));
    [Theory]
    [InlineData("equal")]
    [InlineData("unit")]
    [InlineData("rate")]
    [InlineData("negative-time")]
    public void InvalidTrajectoryRejects(string damage)
        => Assert.ThrowsAny<ArgumentException>(() => new SoftwareTemperatureTrajectory(K(4), new(damage == "equal" ? 4 : 5, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Kelvin), new(damage == "rate" ? 0 : 5, PhysicalUnit.KelvinPerMinute)).At(TimeSpan.FromSeconds(damage == "negative-time" ? -1 : 0)));
}

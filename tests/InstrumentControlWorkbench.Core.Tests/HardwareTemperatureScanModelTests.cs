using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class HardwareTemperatureScanModelTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("equal")]
    [InlineData("turn")]
    [InlineData("unit")]
    [InlineData("rate")]
    [InlineData("tolerance")]
    [InlineData("count")]
    [InlineData("stable-timeout")]
    [InlineData("step-timeout")]
    [InlineData("poll")]
    [InlineData("interval")]
    [InlineData("mode")]
    public void InvalidHardwareRampCannotBecomeAPlan(string damage)
    {
        Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
        Assert.ThrowsAny<ArgumentException>(() => new HardwareTemperatureScanStep(new("scan"), new("temperature"), new("axis"), new(damage == "single" ? [K(4)] : damage == "turn" ? [K(4), K(5), K(4.5)] : [new(4, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Kelvin), new(damage == "equal" ? 4 : 5, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Kelvin)]), new(damage == "rate" ? 0 : 5, PhysicalUnit.KelvinPerMinute), true, K(damage == "tolerance" ? 0 : .1), damage == "count" ? 0 : 3, damage == "stable-timeout" ? TimeSpan.Zero : TimeSpan.FromSeconds(1), damage == "step-timeout" ? TimeSpan.Zero : TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(damage == "poll" ? 49 : 50), damage == "mode" ? (TemperatureRampSampling)99 : TemperatureRampSampling.Threshold, TimeSpan.FromMilliseconds(damage == "interval" ? .5 : 1), new DelayStep(new("body"), TimeSpan.Zero)));
    }
    [Theory]
    [InlineData(TemperatureRampSampling.Threshold)]
    [InlineData(TemperatureRampSampling.EstimatedSetpoint)]
    [InlineData(TemperatureRampSampling.Timed)]
    [InlineData(TemperatureRampSampling.Immediate)]
    public void ProgressTotalDistinguishesFinitePointsTimedEstimateAndImmediateUnknown(TemperatureRampSampling sampling)
    {
        var scan = new HardwareTemperatureScanStep(new("scan"), new("temperature"), new("axis"), new([new(4, PhysicalUnit.Kelvin), new(5, PhysicalUnit.Kelvin)]), new(5, PhysicalUnit.KelvinPerMinute), true, new(.1, PhysicalUnit.Kelvin), 3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), sampling, TimeSpan.FromSeconds(5), new DelayStep(new("body"), TimeSpan.Zero)); Assert.Equal(sampling == TemperatureRampSampling.Immediate ? 0 : sampling == TemperatureRampSampling.Timed ? 4 : 2, scan.ProgressTotal);
    }
}

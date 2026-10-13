using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class TemperatureCoolingModelTests
{
    [Theory]
    [InlineData("ascending")]
    [InlineData("target")]
    [InlineData("end")]
    [InlineData("unit")]
    [InlineData("tolerance")]
    [InlineData("timeout")]
    [InlineData("poll")]
    [InlineData("interval")]
    [InlineData("mode")]
    [InlineData("range")]
    public void UnsafeCoolingConfigurationNeverBecomesAPlan(string damage)
    {
        Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
        Assert.ThrowsAny<ArgumentException>(() => new TemperatureCoolingStep(new("cool"), new("temperature"), new("axis"), new([new(4, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Kelvin), new(damage == "ascending" ? 5 : 3, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Kelvin)]), K(damage == "target" ? 4 : damage == "range" ? .9 : 2), K(damage == "end" ? 4 : 2), K(damage == "tolerance" ? -1 : .05), damage == "timeout" ? TimeSpan.Zero : TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(damage == "poll" ? 49 : 50), damage == "mode" ? (CoolingSamplingMode)99 : CoolingSamplingMode.Threshold, TimeSpan.FromMilliseconds(damage == "interval" ? .5 : 1), new DelayStep(new("body"), TimeSpan.Zero)));
    }
    [Fact]
    public void LowerEndAppendsOneThresholdAndPreservesActualZeroBasedAxisContract()
    {
        Quantity K(double value) => new(value, PhysicalUnit.Kelvin); var scan = new TemperatureCoolingStep(new("cool"), new("temperature"), new("axis"), new([K(4), K(3)]), K(2), K(2), K(.05), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), CoolingSamplingMode.Threshold, TimeSpan.FromMilliseconds(1), new DelayStep(new("body"), TimeSpan.Zero));
        Assert.Equal(new[] { 4.0, 3, 2 }, scan.Thresholds.Values.Select(q => q.Value)); Assert.Equal("axis.index", scan.IndexColumn.Value); Assert.Equal("axis.total", scan.TotalColumn.Value);
    }
}

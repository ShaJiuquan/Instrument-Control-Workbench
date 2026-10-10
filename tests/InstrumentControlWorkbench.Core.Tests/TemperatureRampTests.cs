using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class TemperatureRampTests
{
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private static Quantity Rate(double value) => new(value, PhysicalUnit.KelvinPerMinute);
    [Theory]
    [InlineData(300, 300.2, 5, 83, 3, 300.083, 300.2)]
    [InlineData(300.2, 300, 5, 83, 3, 300.117, 300)]
    [InlineData(1, 1.002, 0.01, 1, 2, 1.001, 1.002)]
    [InlineData(309.99, 310, 20, 333, 1, 310, 310)]
    public void IntegerMilliKelvinPathRetainsDirectionClampedEndpointAndMinimumStep(double start, double target, double rate, long milliStep, long count, double first, double last)
    {
        var path = new TemperatureRampPath(K(start), K(target), Rate(rate));
        Assert.Equal(milliStep, path.StepMilliKelvin); Assert.Equal(count, path.UpdateCount);
        var points = path.Targets().ToArray(); Assert.Equal(first, points[0].Value, 10); Assert.Equal(last, points[^1].Value, 10);
        Assert.All(points, point => Assert.Equal(PhysicalUnit.Kelvin, point.Unit));
    }
    [Fact]
    public void HalfMilliKelvinUsesRustAwayFromZeroRoundingAndEqualRoundedEndpointsAreEmpty()
    {
        var path = new TemperatureRampPath(K(2.0005), K(2.0007), Rate(0.03));
        Assert.Equal(2001, path.StartMilliKelvin); Assert.Equal(2001, path.TargetMilliKelvin);
        Assert.Equal(1, path.StepMilliKelvin); Assert.Empty(path.Targets());
    }
    [Fact]
    public void MaximumRangeIsLazyAndBounded()
    {
        var path = new TemperatureRampPath(K(1), K(310), Rate(0.01));
        Assert.Equal(309_000, path.UpdateCount); Assert.Equal(1.001, path.Targets().First().Value);
    }
    [Theory]
    [InlineData(0.999, 5)]
    [InlineData(310.001, 5)]
    [InlineData(300, 0.009)]
    [InlineData(300, 20.001)]
    public void OutOfRangeTargetsAndRatesReject(double target, double rate)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureRampPath(K(300), K(target), Rate(rate)));
    [Fact]
    public void DimensionallyWrongInputsReject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureRampPath(new(1, PhysicalUnit.Volt), K(300), Rate(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureRampPath(K(1), K(300), new(5, PhysicalUnit.Kelvin)));
    }
}

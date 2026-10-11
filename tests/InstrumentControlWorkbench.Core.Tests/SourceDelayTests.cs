using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SourceDelayTests
{
    [Theory]
    [InlineData(0.001)]
    [InlineData(999999.999)]
    public void DelayIsAnIndependentSecondRegisterAndOtherWritesRetainIt(double seconds)
    {
        var delay = new Quantity(seconds, PhysicalUnit.Second);
        var settings = new Keithley6221Settings(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), sourceDelay: delay);
        Assert.Equal(delay, settings.CaptureParameters()[Keithley6221Parameters.SourceDelay.Id]);
        Assert.Equal(delay, settings.WithParameter(Keithley6221Parameters.Current.Id, new(2e-5, PhysicalUnit.Ampere)).SourceDelay);
        Assert.Equal(delay, settings.WithParameter(Keithley6221Parameters.ManualRange.Id, new(200e-6, PhysicalUnit.Ampere)).WithParameter(Keithley6221Parameters.EnableAutoRange.Id, new(1, PhysicalUnit.Dimensionless)).SourceDelay);
    }
    [Theory]
    [InlineData(0, PhysicalUnit.Second)]
    [InlineData(-1, PhysicalUnit.Second)]
    [InlineData(1000000, PhysicalUnit.Second)]
    [InlineData(1, PhysicalUnit.Volt)]
    public void InvalidDelayRejectsBothSettingsAndConfigurationEvidence(double value, PhysicalUnit unit)
    {
        var delay = new Quantity(value, unit);
        Assert.ThrowsAny<ArgumentException>(() => new Keithley6221Settings(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), sourceDelay: delay));
        Assert.ThrowsAny<ArgumentException>(() => new CurrentExcitationConfiguration(CurrentSourceModel.Keithley6221, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), sourceDelay: delay));
    }
    [Fact]
    public void UnobservedLegacyDelayIsAbsentAndCannotDescribeASmu()
    {
        var settings = new Keithley6221Settings(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt));
        Assert.Null(settings.SourceDelay); Assert.DoesNotContain(Keithley6221Parameters.SourceDelay.Id, settings.CaptureParameters().Keys);
        Assert.Throws<ArgumentException>(() => new CurrentExcitationConfiguration(CurrentSourceModel.Keithley2450, settings.Current, settings.Compliance, sourceDelay: new(1, PhysicalUnit.Second)));
    }
    [Theory]
    [InlineData(1, 0.005, 0.01)]
    [InlineData(2, 0.005, 0.1)]
    public void MeterRangeSelectorUsesObservedInputInsteadOfAssumingInputOne(int channel, double request, double expected)
    {
        var settings = new Keithley2182Settings(channel, null, 1, false, 10, AveragingMode.Repeat, 0.1);
        Assert.Equal(new(expected, PhysicalUnit.Volt), settings.WithParameter(Keithley2182Parameters.RangeSelector.Id, new(request, PhysicalUnit.Volt)).ManualRange);
        if (channel == 2) Assert.Throws<ArgumentException>(() => settings.WithParameter(Keithley2182Parameters.RangeSelector.Id, new(100, PhysicalUnit.Volt)));
    }
}

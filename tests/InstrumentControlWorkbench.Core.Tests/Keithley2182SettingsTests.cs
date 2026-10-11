using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Keithley2182SettingsTests
{
    [Theory]
    [InlineData(1, 0.01)]
    [InlineData(1, 0.1)]
    [InlineData(1, 1)]
    [InlineData(1, 10)]
    [InlineData(1, 100)]
    [InlineData(2, 0.1)]
    [InlineData(2, 1)]
    [InlineData(2, 10)]
    public void NominalRangesRemainExplicitInTheInputConfiguration(int channel, double span)
    {
        var settings = new Keithley2182Settings(channel, new(span, PhysicalUnit.Volt), 50, true, 100, AveragingMode.Moving, 10);
        Assert.False(settings.AutoRange); Assert.Equal(new(span, PhysicalUnit.Volt), settings.CaptureParameters()[Keithley2182Parameters.ManualRange.Id]);
    }
    [Theory]
    [InlineData(0, 1, 10, 0)]
    [InlineData(3, 1, 10, 0)]
    [InlineData(1, 0, 10, 0)]
    [InlineData(1, 50.1, 10, 0)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(1, 1, 101, 0)]
    [InlineData(1, 1, 10, -1)]
    [InlineData(1, 1, 10, 10.1)]
    public void InvalidInputIntegrationOrFilterNeverBecomesAConfiguration(int channel, double nplc, int count, double window)
        => Assert.ThrowsAny<ArgumentException>(() => new Keithley2182Settings(channel, null, nplc, true, count, AveragingMode.Repeat, window));
    [Theory]
    [InlineData(1, 0.02)]
    [InlineData(2, 0.01)]
    [InlineData(2, 100)]
    public void NativeFixedSpansRejectNonNominalOrWrongInputValues(int channel, double span)
        => Assert.ThrowsAny<ArgumentException>(() => new Keithley2182Settings(channel, new(span, PhysicalUnit.Volt), 1, false, 10, AveragingMode.Repeat, 0.1));
    [Theory]
    [InlineData(1, 0.015, 0.1)]
    [InlineData(1, 120, 100)]
    [InlineData(2, 0.01, 0.1)]
    [InlineData(2, 12, 10)]
    public void PpmsExpectedReadingSelectorsResolveUpwardWithDocumentedHeadroom(int channel, double selector, double expected)
        => Assert.Equal(new(expected, PhysicalUnit.Volt), Keithley2182Settings.ResolveRange(channel, new(selector, PhysicalUnit.Volt)));
    [Fact]
    public void ChannelSwitchCannotInventAnUnobservedTargetInputConfiguration()
    {
        var initial = new Keithley2182Settings(1, new(100, PhysicalUnit.Volt), 2, true, 1, AveragingMode.Moving, 0.5);
        Assert.Throws<InvalidOperationException>(() => initial.WithParameter(Keithley2182Parameters.Channel.Id, new(2, PhysicalUnit.Dimensionless)));
        Assert.Equal(initial, initial.WithParameter(Keithley2182Parameters.Channel.Id, new(1, PhysicalUnit.Dimensionless)));
    }
    [Fact]
    public void PassiveMeterCannotCarrySmuStatusOrConnectedDefaultPlaceholders()
    {
        var settings = new Keithley2182Settings(1, null, 1, false, 10, AveragingMode.Repeat, 0.1);
        Assert.Throws<ArgumentException>(() => new AcquisitionQuality(new("read"), new("meter"), new("v"), DateTimeOffset.UtcNow, new(0, 0), meterConfiguration: settings));
        Assert.Throws<ArgumentException>(() => new K2182MeterSnapshot(true, false, null, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new K2182MeterSnapshot(false, true, settings, DateTimeOffset.UtcNow));
        Assert.ThrowsAny<ArgumentException>(() => settings.WithParameter(Keithley2182Parameters.FilterCount.Id, new(2.5, PhysicalUnit.Dimensionless)));
    }
}

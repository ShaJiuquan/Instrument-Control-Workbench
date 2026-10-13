using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Keithley6485SettingsTests
{
    [Theory]
    [InlineData(2e-9)]
    [InlineData(2e-8)]
    [InlineData(2e-7)]
    [InlineData(2e-6)]
    [InlineData(2e-5)]
    [InlineData(2e-4)]
    [InlineData(2e-3)]
    [InlineData(2e-2)]
    public void ExactNominalRangesAndAutorangeAreDistinct(double span)
    {
        var s = new Keithley6485Settings(new(span, PhysicalUnit.Ampere), 60, false, true, true, 100, AveragingMode.Moving);
        Assert.Equal(new(span, PhysicalUnit.Ampere), s.CaptureParameters()[Keithley6485Parameters.ManualRange.Id]);
        var auto = s.WithParameter(Keithley6485Parameters.EnableAutoRange.Id, new(1, PhysicalUnit.Dimensionless));
        Assert.True(auto.AutoRange); Assert.False(auto.CaptureParameters().ContainsKey(Keithley6485Parameters.ManualRange.Id)); Assert.True(auto.ZeroCorrect);
    }
    [Theory]
    [InlineData(0, 10)]
    [InlineData(60.1, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void InvalidIntegrationAndCountsReject(double nplc, int count) => Assert.ThrowsAny<ArgumentException>(() => new Keithley6485Settings(null, nplc, false, false, false, count, AveragingMode.Repeat));
    [Theory]
    [InlineData("k6485.zero_check", 0.5)]
    [InlineData("k6485.averaging_count", 2.5)]
    [InlineData("k6485.manual_range", 3e-9)]
    [InlineData("k6485.averaging_mode", 2)]
    public void InvalidWritesRejectBeforeIo(string parameter, double value)
    {
        var unit = parameter.EndsWith("range", StringComparison.Ordinal) ? PhysicalUnit.Ampere : PhysicalUnit.Dimensionless;
        Assert.ThrowsAny<ArgumentException>(() => Keithley6485Parameters.ValidateWrite(new(parameter), new(value, unit)));
    }
    [Fact]
    public void EvidenceCannotClaimZeroCheckReadOrOtherInstrumentQuality()
    {
        var s = new Keithley6485Settings(null, 1, true, false, false, 1, AveragingMode.Repeat);
        Assert.Throws<ArgumentException>(() => new AcquisitionQuality(new("read"), new("meter"), new("i"), DateTimeOffset.UtcNow, null, picoammeterConfiguration: s));
        Assert.Throws<ArgumentException>(() => new AcquisitionQuality(new("read"), new("meter"), new("i"), DateTimeOffset.UtcNow, new(0, 0), picoammeterConfiguration: s.WithParameter(Keithley6485Parameters.ZeroCheck.Id, new(0, PhysicalUnit.Dimensionless))));
        Assert.Throws<ArgumentException>(() => new K6485MeterSnapshot(true, false, null, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new K6485MeterSnapshot(false, true, s, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k6485"] = new("i") }, [], standaloneVoltageMeters: [new("i")], standaloneCurrentMeters: [new("i")]));
    }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Keithley6514SettingsTests
{
    public static Keithley6514Settings Initial() => new(ElectrometerFunction.Current, new(new(2e-4, PhysicalUnit.Ampere), true), new(new(20, PhysicalUnit.Volt), true), new(new(2e5, PhysicalUnit.Ohm), true), new(new(2e-6, PhysicalUnit.Coulomb), true), 1, true, false, false, 10, AveragingMode.Repeat);
    public static IEnumerable<object[]> NominalSpans() => Enum.GetValues<ElectrometerFunction>().SelectMany(f => Keithley6514Settings.Ranges(f).Select(span => new object[] { f, span }));
    [Theory]
    [MemberData(nameof(NominalSpans))]
    public void AllNominalSpansRemainTypedAndAutorangeRetainsSelectedRegister(ElectrometerFunction function, double span)
    {
        var s = Initial().WithParameter(Keithley6514Parameters.Range(function).Id, new(span, Keithley6514Settings.Unit(function)));
        Assert.False(s.Range(function).AutoRange);
        s = s.WithParameter(Keithley6514Parameters.AutoRange(function).Id, new(1, PhysicalUnit.Dimensionless));
        Assert.True(s.Range(function).AutoRange); Assert.Equal(span, s.CaptureParameters()[Keithley6514Parameters.Range(function).Id].Value);
        s = s.WithParameter(Keithley6514Parameters.AutoRange(function).Id, new(0, PhysicalUnit.Dimensionless)); Assert.Equal(span, s.Range(function).Span.Value);
    }
    [Theory]
    [InlineData(ElectrometerFunction.Current, 3e-11, 2e-10)]
    [InlineData(ElectrometerFunction.Voltage, 3, 20)]
    [InlineData(ElectrometerFunction.Resistance, 3e3, 2e4)]
    [InlineData(ElectrometerFunction.Charge, 3e-8, 2e-7)]
    public void SelectorsResolveUpwardWithoutGuessingUnits(ElectrometerFunction function, double selector, double nominal)
    {
        Assert.Equal(new(nominal, Keithley6514Settings.Unit(function)), Keithley6514Settings.ResolveRange(function, new(selector, Keithley6514Settings.Unit(function))));
        Assert.Throws<ArgumentException>(() => Initial().WithParameter(Keithley6514Parameters.Range(function).Id, new(selector, Keithley6514Settings.Unit(function))));
        Assert.Throws<ArgumentException>(() => Keithley6514Settings.ResolveRange(function, new(selector, PhysicalUnit.Dimensionless)));
    }
    [Fact]
    public void SharedIntegrationAndFilterSurviveFunctionChangesAndInactiveRangeWrites()
    {
        var s = Initial().WithParameter(Keithley6514Parameters.Nplc.Id, new(4, PhysicalUnit.Dimensionless)).WithParameter(Keithley6514Parameters.AveragingCount.Id, new(5, PhysicalUnit.Dimensionless));
        foreach (var f in Enum.GetValues<ElectrometerFunction>())
        { var switched = s.SelectFunction(f); Assert.Equal(4, switched.Nplc); Assert.Equal(5, switched.AveragingCount); Assert.Equal(s.Current, switched.Current); Assert.Equal(s.Charge, switched.Charge); }
        var next = s.WithParameter(Keithley6514Parameters.Range(ElectrometerFunction.Charge).Id, new(2e-8, PhysicalUnit.Coulomb)); Assert.Equal(s.Function, next.Function); Assert.Equal(s.Current, next.Current);
    }
    [Fact]
    public void FunctionChangeNeedsExplicitZeroCheckAndAcquisitionNeedsItOff()
    {
        var current = Initial().WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(0, PhysicalUnit.Dimensionless));
        Assert.Equal(current, current.SelectFunction(ElectrometerFunction.Current)); Assert.Throws<InvalidOperationException>(() => current.SelectFunction(ElectrometerFunction.Charge));
        Assert.Throws<ArgumentException>(() => Initial().ValidateReading(ElectrometerFunction.Current, new(1e-12, PhysicalUnit.Ampere)));
        var charge = Initial().SelectFunction(ElectrometerFunction.Charge).WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(0, PhysicalUnit.Dimensionless)); charge.ValidateReading(ElectrometerFunction.Charge, new(-1e-9, PhysicalUnit.Coulomb));
        Assert.Throws<ArgumentException>(() => charge.ValidateReading(ElectrometerFunction.Current, new(1e-12, PhysicalUnit.Ampere)));
    }
    [Theory]
    [InlineData("k6514.function", 1.5)]
    [InlineData("k6514.zero_check", 0.5)]
    [InlineData("k6514.averaging_count", 101)]
    [InlineData("k6514.nplc", 10.1)]
    public void InvalidControlsReject(string id, double value) => Assert.ThrowsAny<ArgumentException>(() => Keithley6514Parameters.ValidateWrite(new(id), new(value, PhysicalUnit.Dimensionless)));
    [Theory]
    [InlineData(ElectrometerFunction.Current, 0.022)]
    [InlineData(ElectrometerFunction.Voltage, 211)]
    [InlineData(ElectrometerFunction.Resistance, -1)]
    [InlineData(ElectrometerFunction.Resistance, 2.11e11)]
    [InlineData(ElectrometerFunction.Charge, 2.11e-5)]
    public void FunctionSpecificOverflowAndNegativeOhmsReject(ElectrometerFunction function, double value)
    {
        var s = Initial().SelectFunction(function).WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(0, PhysicalUnit.Dimensionless));
        Assert.Throws<ArgumentException>(() => s.ValidateReading(function, new(value, Keithley6514Settings.Unit(function))));
    }
    [Fact]
    public void EvidenceRejectsZeroCheckAndOtherInstrumentSettings()
    {
        Assert.Throws<ArgumentException>(() => new AcquisitionQuality(new("s"), new("d"), new("q"), DateTimeOffset.UtcNow, null, electrometerConfiguration: Initial()));
        var off = Initial().WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(0, PhysicalUnit.Dimensionless));
        Assert.Throws<ArgumentException>(() => new AcquisitionQuality(new("s"), new("d"), new("q"), DateTimeOffset.UtcNow, new(0, 0), electrometerConfiguration: off));
    }
}

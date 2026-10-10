using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SourceRangeModelTests
{
    public static IEnumerable<object[]> Ranges => Enum.GetValues<SourceFunction>().SelectMany(f => Keithley2450SourceRanges.For(f).Select(q => new object[] { f, q.Value }));
    [Theory]
    [MemberData(nameof(Ranges))]
    public void EveryNominalRangeAcceptsSignedHeadroomAndRejectsAnOversizedLevel(SourceFunction function, double nominal)
    {
        var range = new Quantity(nominal, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere);
        foreach (var sign in new[] { -1, 1 })
        {
            Keithley2450Limits.Validate(new SourceSetpoint(function, sign * nominal * 1.05, function == SourceFunction.Voltage ? 105e-6 : 21, range));
            Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SourceRanges.ValidateLevel(function, sign * nominal * 1.051, range));
        }
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, 0.05, 0.2)]
    [InlineData(SourceFunction.Voltage, 3, 20)]
    [InlineData(SourceFunction.Current, 0.00005, 0.0001)]
    [InlineData(SourceFunction.Current, 0.2, 1)]
    public void ApproximatePpmsSelectorsChooseTheNextNominalFullScale(SourceFunction function, double selector, double expected)
    {
        var unit = function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere;
        Assert.Equal(new(expected, unit), Keithley2450SourceRanges.Resolve(function, new(selector, unit)));
    }
    [Fact]
    public void UnitsNominalChoicesAndSelectorBoundsCannotBeCoerced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SourceRanges.Validate(SourceFunction.Voltage, new(1, PhysicalUnit.Volt)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SourceRanges.Validate(SourceFunction.Voltage, new(0.02, PhysicalUnit.Ampere)));
        foreach (var value in new[] { 0.0, -1, 201 }) Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SourceRanges.Resolve(SourceFunction.Voltage, new(value, PhysicalUnit.Volt)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SourceRanges.For((SourceFunction)99));
        Assert.Throws<NotSupportedException>(() => ((IList<Quantity>)Keithley2450SourceRanges.For(SourceFunction.Voltage))[0] = new(1, PhysicalUnit.Volt));
    }
    [Fact]
    public void AutomaticSourceRangePreservesTheExistingCoupledEnvelope()
    {
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 210, 0.105));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 100, 1)));
        Assert.Null(new SourceSetpoint(SourceFunction.Voltage, 210, 0.105).ManualRange);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, 200, 1)]
    [InlineData(SourceFunction.Current, 1, 100)]
    public void HighFullScaleRangeRestrictsComplianceEvenAtZeroLevel(SourceFunction function, double range, double compliance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.Validate(new SourceSetpoint(function, 0, compliance, new(range, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere))));
    }
}

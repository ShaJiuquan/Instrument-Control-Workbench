using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SenseRangeModelTests
{
    public static IEnumerable<object[]> NominalRanges()
    {
        foreach (var function in Enum.GetValues<SenseFunction>())
            foreach (var range in Keithley2450SenseRanges.For(function)) yield return [function, range.Value, range.Unit];
    }
    [Theory]
    [MemberData(nameof(NominalRanges))]
    public void AllDeclaredNominalSpansAreTypedAndImmutable(SenseFunction function, double value, PhysicalUnit unit)
    {
        Keithley2450Limits.Validate(new SenseConfiguration(function, SensingMode.FourWire, 1, false, new(value, unit)));
        Assert.Equal(new(value, unit), Keithley2450SenseRanges.Resolve(function, new(value, unit)));
        Assert.Throws<NotSupportedException>(() => ((IList<Quantity>)Keithley2450SenseRanges.For(function))[0] = new(value, unit));
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, SenseFunction.Current, 0.001, PhysicalUnit.Ampere)]
    [InlineData(SourceFunction.Current, SenseFunction.Voltage, 20, PhysicalUnit.Volt)]
    public void FixedSpanRejectsImplicitLimitAdjustmentAtEitherBoundary(SourceFunction sourceFunction, SenseFunction senseFunction, double range, PhysicalUnit unit)
    {
        var sense = new SenseConfiguration(senseFunction, SensingMode.FourWire, 1, false, new(range, unit));
        Keithley2450Limits.ValidateConfiguration(new(sourceFunction, 0, range * 1.05), sense);
        Keithley2450Limits.ValidateConfiguration(new(sourceFunction, 0, range * 0.0011), sense);
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.ValidateConfiguration(new(sourceFunction, 0, range * 0.001), sense));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.ValidateConfiguration(new(sourceFunction, 0, range * 1.051), sense));
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, 200, SenseFunction.Current, 1, PhysicalUnit.Ampere, 0.1)]
    [InlineData(SourceFunction.Current, 1, SenseFunction.Voltage, 200, PhysicalUnit.Volt, 21)]
    public void NominalSourceAndSenseRangesCannotExceedThePowerEnvelopeAtZeroLevel(SourceFunction sourceFunction, double sourceRange, SenseFunction senseFunction, double senseRange, PhysicalUnit unit, double compliance)
    {
        var source = new SourceSetpoint(sourceFunction, 0, compliance, new(sourceRange, sourceFunction == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.ValidateConfiguration(source, new(senseFunction, SensingMode.FourWire, 1, false, new(senseRange, unit))));
    }
    [Fact]
    public void InvalidUnitsModesSelectorsAndUnsupportedOhmmeterCannotBeAdvertisedAsIndependentRanges()
    {
        Assert.Throws<ArgumentException>(() => Keithley2450Limits.Validate(new SenseConfiguration(SenseFunction.Current, SensingMode.TwoWire, 1, true, new(0.001, PhysicalUnit.Ampere))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SenseRanges.Validate(SenseFunction.Current, new(0.001, PhysicalUnit.Volt)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SenseRanges.Validate(SenseFunction.Current, new(0.0005, PhysicalUnit.Ampere)));
        foreach (var value in new[] { 0d, -1, 2 }) Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SenseRanges.Resolve(SenseFunction.Current, new(value, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450SenseRanges.For((SenseFunction)99));
        Assert.Throws<NotSupportedException>(() => Keithley2450Limits.ValidateConfiguration(new(SourceFunction.Voltage, 0, 0.0001), new(SenseFunction.Voltage, SensingMode.TwoWire, 1, false, new(20, PhysicalUnit.Volt))));
        Assert.Throws<NotSupportedException>(() => Keithley2450Limits.ValidateConfiguration(new(SourceFunction.Voltage, 0, 0.0001), new(SenseFunction.Resistance, SensingMode.TwoWire, 1, false, new(200000, PhysicalUnit.Ohm))));
        Assert.Equal(ParameterAccess.Read, Keithley2450Parameters.ManualSenseResistanceRange.Access);
        Assert.Equal(new(0.001, PhysicalUnit.Ampere), Keithley2450SenseRanges.Resolve(SenseFunction.Current, new(0.0005, PhysicalUnit.Ampere)));
        Keithley2450Limits.Validate(new SenseConfiguration(SenseFunction.Current, SensingMode.TwoWire, 1, false));
    }
}

using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Keithley2450LimitsTests
{
    [Fact]
    public void UnknownFunctionAndWiringModesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.Validate(new SourceSetpoint((SourceFunction)999, 0.1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.Validate(new SenseConfiguration((SenseFunction)999, SensingMode.FourWire, 1, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.Validate(new SenseConfiguration(SenseFunction.Current, (SensingMode)999, 1, true)));
    }

    [Fact]
    public void VoltageSourceUsesCurrentComplianceLimit()
    {
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 210.0, 0.105));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 211.0, 1e-3)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 1.0, 0.0)));
    }

    [Theory]
    [InlineData(SourceFunction.Voltage, 100, 1)]
    [InlineData(SourceFunction.Voltage, -100, 1)]
    [InlineData(SourceFunction.Current, 1, 100)]
    [InlineData(SourceFunction.Current, -1, 100)]
    public void CoupledSourceComplianceEnvelopeIsRejected(SourceFunction function, double level, double compliance)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Limits.Validate(new SourceSetpoint(function, level, compliance)));

    [Fact]
    public void CurrentSourceUsesVoltageComplianceLimit()
    {
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, 1.05, 21.0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, 1.2, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, 0.1, 0.01)));
    }
}

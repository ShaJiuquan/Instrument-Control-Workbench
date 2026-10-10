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
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 210.0, 1.05));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 211.0, 1e-3)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, 1.0, 0.0)));
    }

    [Fact]
    public void CurrentSourceUsesVoltageComplianceLimit()
    {
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, 1.05, 210.0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, 1.2, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, 0.1, 0.01)));
    }
}

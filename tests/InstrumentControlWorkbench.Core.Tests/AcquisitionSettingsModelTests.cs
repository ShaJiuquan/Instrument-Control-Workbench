using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class AcquisitionSettingsModelTests
{
    [Theory]
    [InlineData(0, AveragingMode.Repeat)]
    [InlineData(101, AveragingMode.Repeat)]
    [InlineData(-1, AveragingMode.Repeat)]
    [InlineData(10, (AveragingMode)99)]
    public void InvalidProfileIsRejected(int count, AveragingMode mode) => Assert.Throws<ArgumentOutOfRangeException>(() => new Keithley2450AcquisitionSettings(true, false, true, count, mode));
    [Theory]
    [InlineData(1, AveragingMode.Repeat)]
    [InlineData(100, AveragingMode.Moving)]
    public void NativeBoundariesAndExplicitResistanceApplicability(int count, AveragingMode mode)
    {
        var profile = new Keithley2450AcquisitionSettings(false, true, true, count, mode);
        Assert.Equal(5, profile.CaptureParameters(SenseFunction.Current).Count());
        Assert.Equal(4, profile.CaptureParameters(SenseFunction.Resistance).Count());
        Assert.Throws<NotSupportedException>(() => Keithley2450Limits.Validate(new SenseConfiguration(SenseFunction.Resistance, SensingMode.TwoWire, 1, true, Acquisition: profile)));
    }
    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1, 0.5)]
    [InlineData(2, 0.5)]
    [InlineData(3, 2.5)]
    [InlineData(3, 101)]
    [InlineData(4, 0.5)]
    [InlineData(4, 2)]
    public void ParameterCodesAreIntegralAndBounded(int index, double value)
    {
        var profile = new Keithley2450AcquisitionSettings(true, false, false, 10, AveragingMode.Repeat);
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.WithParameter(Parameters[index].Id, new(value, PhysicalUnit.Dimensionless)));
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 20)]
    [InlineData(4, 1)]
    public void IndividualChangeProducesANewImmutableProfileAndCapturesTypedParameters(int index, double value)
    {
        var before = new Keithley2450AcquisitionSettings(true, false, false, 10, AveragingMode.Repeat); var after = before.WithParameter(Parameters[index].Id, new(value, PhysicalUnit.Dimensionless));
        Assert.NotEqual(before, after); Assert.Equal(new Keithley2450AcquisitionSettings(true, false, false, 10, AveragingMode.Repeat), before);
        Assert.Equal(new(value, PhysicalUnit.Dimensionless), Assert.Single(after.CaptureParameters(SenseFunction.Voltage), p => p.Descriptor.Id == Parameters[index].Id).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => before.WithParameter(Parameters[index].Id, new(value, PhysicalUnit.Volt)));
    }
    [Fact]
    public void RefreshIsAnActionRatherThanAPersistentAutoZeroMode()
    {
        Assert.Equal(ParameterAccess.Write, Keithley2450Parameters.RefreshZero.Access); Assert.False(Keithley2450AcquisitionSettings.IsParameter(Keithley2450Parameters.RefreshZero.Id));
        Keithley2450Parameters.RefreshZero.ValidateWrite(new(1, PhysicalUnit.Dimensionless));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keithley2450Parameters.RefreshZero.ValidateWrite(new(0, PhysicalUnit.Dimensionless)));
    }
    private static ParameterDescriptor[] Parameters => [Keithley2450Parameters.AutoZero, Keithley2450Parameters.AutorangeRebound, Keithley2450Parameters.AveragingEnabled, Keithley2450Parameters.AveragingCount, Keithley2450Parameters.AveragingMode];
}

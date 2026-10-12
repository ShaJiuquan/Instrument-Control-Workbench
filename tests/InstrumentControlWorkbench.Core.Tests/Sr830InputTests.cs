using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Sr830InputTests
{
    [Theory]
    [InlineData(Sr830InputMode.VoltageA)]
    [InlineData(Sr830InputMode.VoltageAB)]
    [InlineData(Sr830InputMode.Current1M)]
    [InlineData(Sr830InputMode.Current100M)]
    public void FrozenFramePreservesReportedRawMagnitudeAndTypedInputSubset(Sr830InputMode input)
    {
        var settings = new Sr830InputSettings(input, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12);
        var frame = new Sr830RawFrame(new(.001, PhysicalUnit.Volt), new(-.002, PhysicalUnit.Volt), new(.0023, PhysicalUnit.Volt), new(-63, PhysicalUnit.Degree), DateTimeOffset.UtcNow);
        var step = new ReadSr830FrameStep(new("read"), new("lockin"), input, new("signal")); var values = step.CaptureValues(new(frame, settings));
        Assert.Equal(8, values.Count); Assert.Equal(frame.R, values[new("signal.r_v")]); Assert.NotEqual(Math.Sqrt(5) * .001, values[new("signal.r_v")].Value); Assert.Equal(new((int)input, PhysicalUnit.Dimensionless), values[new("signal.input_mode")]);
    }
    [Fact]
    public void NominalSensitivityAndTimeConstantTablesRoundTripWithoutInterpolation()
    {
        Assert.Equal(27, Sr830InputSettings.Sensitivities.Count); Assert.Equal(20, Sr830InputSettings.TimeConstants.Count);
        var settings = new Sr830InputSettings(Sr830InputMode.VoltageAB, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12);
        foreach (var value in Sr830InputSettings.Sensitivities) Assert.Equal(new(value, PhysicalUnit.Volt), settings.WithParameter(Sr830Parameters.Sensitivity.Id, new(value, PhysicalUnit.Volt)).Sensitivity);
        foreach (var value in Sr830InputSettings.TimeConstants) Assert.Equal(new(value, PhysicalUnit.Second), settings.WithParameter(Sr830Parameters.TimeConstant.Id, new(value, PhysicalUnit.Second)).TimeConstant);
    }
    [Theory]
    [InlineData("sensitivity")]
    [InlineData("time")]
    [InlineData("slope")]
    [InlineData("gain")]
    [InlineData("mode")]
    [InlineData("unit")]
    public void InvalidInputProfilesAreRejectedBeforeTheyReachADriver(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Sr830InputSettings(damage == "mode" ? (Sr830InputMode)4 : damage == "gain" ? Sr830InputMode.Current100M : Sr830InputMode.VoltageAB,
            new(damage == "sensitivity" ? .015 : damage == "gain" ? .02 : .01, damage == "unit" ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(damage == "time" ? .2 : .1, PhysicalUnit.Second), damage == "slope" ? 7 : 12));
    }
    [Theory]
    [InlineData("negative-r")]
    [InlineData("phase")]
    [InlineData("x-unit")]
    [InlineData("theta-unit")]
    public void MalformedRawFramesCannotBeConstructed(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Sr830RawFrame(new(.001, damage == "x-unit" ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(0, PhysicalUnit.Volt), new(damage == "negative-r" ? -.01 : .002, PhysicalUnit.Volt), new(damage == "phase" ? 181 : 0, damage == "theta-unit" ? PhysicalUnit.Volt : PhysicalUnit.Degree), DateTimeOffset.UtcNow));
    }
    [Fact]
    public void AFrameCannotClaimAnInputModeDifferentFromItsFrozenStep()
    {
        var step = new ReadSr830FrameStep(new("read"), new("meter"), Sr830InputMode.VoltageA, new("raw"));
        Assert.Throws<ArgumentException>(() => step.CaptureValues(new(new(new(0, PhysicalUnit.Volt), new(0, PhysicalUnit.Volt), new(0, PhysicalUnit.Volt), new(0, PhysicalUnit.Degree), DateTimeOffset.UtcNow), new(Sr830InputMode.VoltageAB, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12))));
    }
}

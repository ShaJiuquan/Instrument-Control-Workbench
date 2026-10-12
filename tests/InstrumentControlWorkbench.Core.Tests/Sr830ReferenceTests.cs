using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Sr830ReferenceTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("frequency")]
    [InlineData("frequency-unit")]
    [InlineData("amplitude")]
    [InlineData("harmonic")]
    [InlineData("product")]
    public void InvalidReadbackCannotBecomeAReferenceObservation(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Sr830ReferenceReadback(damage == "source" ? (Sr830ReferenceSource)2 : Sr830ReferenceSource.Internal,
            new(damage == "frequency" ? 0 : damage == "product" ? 102000 : 13.7, damage == "frequency-unit" ? PhysicalUnit.Second : PhysicalUnit.Hertz), new(damage == "amplitude" ? 0 : .004, PhysicalUnit.Volt), damage == "harmonic" ? 0 : damage == "product" ? 2 : 1));
    }
    [Theory]
    [InlineData(Sr830InputMode.VoltageA, 1d, PhysicalUnit.Volt)]
    [InlineData(Sr830InputMode.VoltageAB, 1d, PhysicalUnit.Volt)]
    [InlineData(Sr830InputMode.Current1M, 1e6, PhysicalUnit.Ampere)]
    [InlineData(Sr830InputMode.Current100M, 1e8, PhysicalUnit.Ampere)]
    public void InputReferralPreservesRawFrameAndRecordsNominalGain(Sr830InputMode mode, double gain, PhysicalUnit unit)
    {
        var frameStep = new ReadSr830FrameStep(new("raw"), new("meter"), mode, new("raw"), true);
        var reference = new Sr830ReferenceReadback(Sr830ReferenceSource.Internal, new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt), 1);
        var observation = new Sr830FrameObservation(new(new(.001, PhysicalUnit.Volt), new(-.002, PhysicalUnit.Volt), new(.0023, PhysicalUnit.Volt), new(-63, PhysicalUnit.Degree), DateTimeOffset.UtcNow), new(mode, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12), reference);
        var values = frameStep.CaptureValues(observation); Assert.Equal(12, values.Count);
        var step = Sr830InputReferredAnalysis.Create(new("refer"), frameStep, new("input")); var raw = new MeasurementDataRow(observation.Frame.CapturedAt, values); var result = CalculationSummary.BuildRow(step, 1, raw);
        Assert.Equal(new(.001 / gain, unit), result.Values[new("input.input_x")]); Assert.Equal(new(-.002 / gain, unit), result.Values[new("input.input_y")]); Assert.Equal(new(.0023 / gain, unit), result.Values[new("input.input_r")]);
        Assert.Equal(raw.Values[new("raw.r_v")], result.Values[new("raw.r_v")]); Assert.Equal(reference.SineAmplitude, result.Values[new("raw.sine_amplitude_v")]);
        Assert.Equal(mode is Sr830InputMode.Current1M or Sr830InputMode.Current100M, result.Values.ContainsKey(new("input.nominal_transimpedance_ohm")));
    }
    [Theory]
    [InlineData(ArithmeticOperator.Divide, 4e-9, PhysicalUnit.Ampere, .004, PhysicalUnit.Volt, 1e-6, PhysicalUnit.Siemens)]
    [InlineData(ArithmeticOperator.Multiply, 1e-6, PhysicalUnit.Siemens, .004, PhysicalUnit.Volt, 4e-9, PhysicalUnit.Ampere)]
    [InlineData(ArithmeticOperator.Divide, 1, PhysicalUnit.Dimensionless, 1e6, PhysicalUnit.Ohm, 1e-6, PhysicalUnit.Siemens)]
    [InlineData(ArithmeticOperator.Divide, 1, PhysicalUnit.Dimensionless, 1e-6, PhysicalUnit.Siemens, 1e6, PhysicalUnit.Ohm)]
    [InlineData(ArithmeticOperator.Multiply, 1e-6, PhysicalUnit.Siemens, 1e6, PhysicalUnit.Ohm, 1, PhysicalUnit.Dimensionless)]
    public void ConductanceRelationsRemainTypedAndScaled(ArithmeticOperator operation, double left, PhysicalUnit leftUnit, double right, PhysicalUnit rightUnit, double expected, PhysicalUnit resultUnit)
    {
        var value = QuantityArithmetic.Evaluate(operation, new(left, leftUnit), new(right, rightUnit)); Assert.Equal(resultUnit, value.Unit); Assert.Equal(expected, value.Value, 14);
    }
    [Fact]
    public void ReferenceColumnsNeverUseInventedDefaultReadbacks()
    {
        var step = new ReadSr830FrameStep(new("frame"), new("meter"), Sr830InputMode.VoltageAB, new("raw"), true);
        var observation = new Sr830FrameObservation(new(new(0, PhysicalUnit.Volt), new(0, PhysicalUnit.Volt), new(0, PhysicalUnit.Volt), new(0, PhysicalUnit.Degree), DateTimeOffset.UtcNow), new(Sr830InputMode.VoltageAB, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12));
        Assert.Throws<ArgumentException>(() => step.CaptureValues(observation)); Assert.Equal(8, new ReadSr830FrameStep(new("old"), new("meter"), Sr830InputMode.VoltageAB, new("raw")).Channels.Count);
    }
    [Fact]
    public void ReciprocalAndConductanceCalculationsKeepNumericFailurePolicy()
    {
        Assert.Throws<DivideByZeroException>(() => QuantityArithmetic.Evaluate(ArithmeticOperator.Divide, new(1, PhysicalUnit.Dimensionless), new(0, PhysicalUnit.Ohm)));
        Assert.Throws<ArithmeticException>(() => QuantityArithmetic.Evaluate(ArithmeticOperator.Divide, new(1e300, PhysicalUnit.Ampere), new(1e-300, PhysicalUnit.Volt)));
    }
}

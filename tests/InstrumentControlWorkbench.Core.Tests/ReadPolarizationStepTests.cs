using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ReadPolarizationStepTests
{
    [Theory]
    [InlineData("unit")]
    [InlineData("duration")]
    [InlineData("voltage")]
    [InlineData("compliance")]
    [InlineData("range")]
    [InlineData("sense")]
    [InlineData("columns")]
    [InlineData("attempts")]
    public void InvalidPulseCannotBecomeAPlanStep(string damage)
    {
        var sense = new SenseConfiguration(damage == "sense" ? SenseFunction.Voltage : SenseFunction.Current, SensingMode.TwoWire, 1, true);
        Assert.ThrowsAny<ArgumentException>(() => new ReadPolarizationStep(new("pulse"), new("source"), new(damage == "voltage" ? 211 : damage == "range" ? 100 : 5, damage == "unit" ? PhysicalUnit.Ampere : PhysicalUnit.Volt), damage == "duration" ? TimeSpan.FromMilliseconds(-1) : TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.Zero, new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(damage is "range" or "compliance" ? 1 : .001, damage == "compliance" ? PhysicalUnit.Volt : PhysicalUnit.Ampere), sense, new("v"), new(damage == "columns" ? "v" : "i"), new("attempts"), damage == "attempts" ? 101 : 8));
    }
    [Fact]
    public void PulseIsAUnitAwareMeasurementLeafForPointsAndCalculations()
    {
        var step = new ReadPolarizationStep(new("pulse"), new("source"), new(5, PhysicalUnit.Volt), TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.Zero, new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true), new("v"), new("i"), new("attempts"));
        Assert.Equal(new[] { step.VoltageColumn, step.CurrentColumn, step.AttemptsColumn }, new MeasurementGroupMember("Pulse", step).OutputColumns); Assert.Equal(SourceFunction.Voltage, step.Setpoint(step.PolarizationVoltage).Function); Assert.Throws<ArgumentException>(() => step.Setpoint(new(5, PhysicalUnit.Ampere)));
    }
}

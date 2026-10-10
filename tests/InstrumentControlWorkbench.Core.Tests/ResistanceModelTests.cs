using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ResistanceModelTests
{
    [Fact]
    public void RecipeRetainsTypedLimitsWiringAndRustZeroCurrentThreshold()
    {
        var recipe = new VoltageResistanceRecipe("R", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere));
        Assert.Equal(SensingMode.TwoWire, recipe.Sense.Mode);
        Assert.Equal(1, recipe.Sense.Nplc);
        Assert.Equal(TimeSpan.FromSeconds(0.1), recipe.Settle);
        Assert.Equal(2.220446049250313e-16, recipe.MinimumCurrent.Value);
        Assert.Throws<ArgumentException>(() => new VoltageResistanceRecipe("R", new(0.1, PhysicalUnit.Ampere), new(1e-4, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentException>(() => new VoltageResistanceRecipe("R", new(201, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoltageResistanceRecipe("R", new(100, PhysicalUnit.Volt), new(1, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoltageResistanceRecipe("R", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), settle: TimeSpan.FromSeconds(-1)));
    }
    [Fact]
    public void ReadingAndStepRejectMismatchedUnitsOrAmbiguousColumns()
    {
        Assert.Throws<ArgumentException>(() => new SourceSenseReading(SourceFunction.Voltage, SenseFunction.Current,
            new(0.1, PhysicalUnit.Ampere), new(1e-5, PhysicalUnit.Ampere), DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new ReadResistanceStep(new("read"), new("k2450"), SourceFunction.Voltage,
            new("v"), new("v"), new("r"), new(0, PhysicalUnit.Ampere)));
        Assert.Throws<ArgumentException>(() => new ReadResistanceStep(new("read"), new("k2450"), SourceFunction.Voltage,
            new("v"), new("i"), new("r"), new(-1, PhysicalUnit.Ampere)));
    }
}

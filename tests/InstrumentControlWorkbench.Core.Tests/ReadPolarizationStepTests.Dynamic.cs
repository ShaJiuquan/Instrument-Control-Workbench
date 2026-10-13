using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class DynamicPolarizationStepTests
{
    private static ReadPolarizationStep Create(ParameterId? requested, PlanValue target) => new(new("pulse"), new("source"), new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.Zero, new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true), new("v"), new("i"), new("attempts"), requestedPolarizationColumn: requested, polarizationTarget: target);
    [Fact]
    public void DynamicTargetRequiresExplicitRequestedVoltageEvidence()
        => Assert.Throws<ArgumentException>(() => Create(null, new VariableValue(new("axis"))));
    [Fact]
    public void RequestedColumnCannotReplaceAnOriginalAcquisitionColumn()
        => Assert.Throws<ArgumentException>(() => Create(new("i"), new VariableValue(new("axis"))));
    [Fact]
    public void CompatibilityConstantAccessorCannotInventADynamicValue()
    {
        var pulse = Create(new("requested"), new VariableValue(new("axis"))); Assert.IsType<VariableValue>(pulse.PolarizationTarget); Assert.Throws<InvalidOperationException>(() => pulse.PolarizationVoltage);
        Assert.Equal(new[] { new ParameterId("v"), new("i"), new("attempts"), new("requested") }, new MeasurementGroupMember("Dynamic pulse", pulse).OutputColumns);
    }
    [Fact]
    public void DynamicRecipeCannotStoreAnInvalidConfigurationSeed()
        => Assert.ThrowsAny<ArgumentException>(() => new ReadPolarizationStep(new("pulse"), new("source"), new(999, PhysicalUnit.Volt), TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.Zero, new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true), new("v"), new("i"), new("attempts"), requestedPolarizationColumn: new("requested"), polarizationTarget: new VariableValue(new("axis"))));
    [Fact]
    public void ConflictingConstantTargetAndConfigurationReject()
        => Assert.Throws<ArgumentException>(() => Create(new("requested"), new ConstantValue(new(2, PhysicalUnit.Volt))));
}

using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class VariableControlStepTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000001)]
    public void RejectsInvalidPlanExecutionBudget(long maximum) => Assert.Throws<ArgumentOutOfRangeException>(() => new ExperimentPlan("Budget", new DelayStep(new("body"), TimeSpan.Zero), maximumStepExecutions: maximum));

    [Fact]
    public void PlanBudgetDefaultsToExistingTenMillionLimit()
    {
        var plan = new ExperimentPlan("Default", new DelayStep(new("body"), TimeSpan.Zero));
        Assert.Equal(10_000_000, plan.MaximumStepExecutions);
    }

    [Fact]
    public void MutationRequiresVariableKnownActionAndDimensionlessMultiplier()
    {
        Assert.Throws<ArgumentNullException>(() => new MutateValueStep(new("change"), null!, VariableMutation.Set, new(0, PhysicalUnit.Dimensionless)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MutateValueStep(new("change"), new("value"), (VariableMutation)99, new(0, PhysicalUnit.Dimensionless)));
        Assert.Throws<ArgumentException>(() => new MutateValueStep(new("change"), new("value"), VariableMutation.Multiply, new(2, PhysicalUnit.Volt)));
    }

    [Fact]
    public void CompositesRequireBodyAndWhileCondition()
    {
        var condition = new ValueCondition(new("value"), ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless)));
        var body = new DelayStep(new("body"), TimeSpan.Zero);
        Assert.Throws<ArgumentNullException>(() => new WhileStep(new("loop"), null!, body));
        Assert.Throws<ArgumentNullException>(() => new WhileStep(new("loop"), condition, null!));
        Assert.Throws<ArgumentNullException>(() => new OutcomeMutationStep(new("hook"), null!));
    }
}

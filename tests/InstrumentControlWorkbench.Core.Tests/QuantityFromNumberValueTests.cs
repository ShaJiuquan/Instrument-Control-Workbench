using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class QuantityFromNumberValueTests
{
    [Fact]
    public void RequiresAValidVariableAndUnit()
    {
        Assert.Throws<ArgumentNullException>(() => new QuantityFromNumberValue(null!, PhysicalUnit.Tesla));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuantityFromNumberValue(new("number"), (PhysicalUnit)99));
    }

    [Fact]
    public void PreservesExplicitQuantityContract()
    {
        var variable = new ParameterId("number"); var value = new QuantityFromNumberValue(variable, PhysicalUnit.Tesla);
        Assert.Same(variable, value.Variable); Assert.Equal(PhysicalUnit.Tesla, value.Unit);
    }
}

using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class IterationIndexBaseTests
{
    [Fact]
    public void ExistingConstructorDefaultsToOneBased()
    {
        var loop = new ForEachStep(new("loop"), new("value"), new([new(0, PhysicalUnit.Dimensionless)]), new DelayStep(new("body"), TimeSpan.Zero));
        Assert.Equal(IterationIndexBase.OneBased, loop.IndexBase);
    }

    [Theory]
    [InlineData(IterationIndexBase.ZeroBased)]
    [InlineData(IterationIndexBase.OneBased)]
    public void KeepsExplicitBase(IterationIndexBase indexBase)
    {
        var loop = new ForEachStep(new("loop"), new("value"), new([new(0, PhysicalUnit.Dimensionless)]), new DelayStep(new("body"), TimeSpan.Zero), indexBase: indexBase);
        Assert.Equal(indexBase, loop.IndexBase);
    }

    [Fact]
    public void RejectsUnknownIndexBase()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForEachStep(new("loop"), new("value"), new([new(0, PhysicalUnit.Dimensionless)]), new DelayStep(new("body"), TimeSpan.Zero), indexBase: (IterationIndexBase)99));
    }
}

using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ThresholdSwitchTests
{
    [Theory]
    [InlineData(-1, 3)]
    [InlineData(0, 1)]
    [InlineData(4.9999999999, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    [InlineData(100, 2)]
    public void DeclarationOrderAndInclusiveBoundsSelectExactlyOneChild(double value, int index)
    {
        var choice = new ThresholdSwitchStep(new("switch"), new("value"),
            Enumerable.Range(0, 4).Select(i => new DelayStep(new("child" + i), TimeSpan.Zero)),
            [new(10, PhysicalUnit.Tesla), new(0, PhysicalUnit.Tesla), new(5, PhysicalUnit.Tesla)]);
        Assert.Equal(index, choice.SelectBranch(new(value, PhysicalUnit.Tesla)));
        Assert.Throws<ArgumentException>(() => choice.SelectBranch(new(value, PhysicalUnit.Ampere)));
    }

    [Fact]
    public void LastChildCanBeBothACaseAndDefaultAndDuplicateThresholdsKeepTheirOrder()
    {
        var choice = new ThresholdSwitchStep(new("switch"), new("value"),
            [new DelayStep(new("first"), TimeSpan.Zero), new DelayStep(new("last"), TimeSpan.Zero)],
            [new(2, PhysicalUnit.Dimensionless), new(2, PhysicalUnit.Dimensionless)]);
        Assert.Equal(1, choice.SelectBranch(new(-1, PhysicalUnit.Dimensionless)));
        Assert.Equal(1, choice.SelectBranch(new(2, PhysicalUnit.Dimensionless)));
        var defaultOnly = new ThresholdSwitchStep(new("default"), new("value"), choice.Children, []);
        Assert.Equal(1, defaultOnly.SelectBranch(new(100, PhysicalUnit.Dimensionless)));
        Assert.Null(new ThresholdSwitchStep(new("empty"), new("unused"), [], []).SelectBranch(new(0, PhysicalUnit.Dimensionless)));
    }

    [Fact]
    public void ConstructorFreezesBoundedBranchesAndRejectsIncoherentThresholds()
    {
        ExperimentStep[] branches = [new DelayStep(new("original"), TimeSpan.Zero)]; Quantity[] values = [new(1, PhysicalUnit.Volt)];
        var choice = new ThresholdSwitchStep(new("switch"), new("value"), branches, values);
        branches[0] = new DelayStep(new("replacement"), TimeSpan.Zero); values[0] = new(2, PhysicalUnit.Volt);
        Assert.Equal(new StepId("original"), choice.Children[0].Id); Assert.Equal(1, choice.Thresholds[0].Value);
        Assert.Throws<NotSupportedException>(() => ((IList<Quantity>)choice.Thresholds).Clear());
        Assert.Throws<ArgumentException>(() => new ThresholdSwitchStep(new("bad"), new("value"), [], values));
        Assert.Throws<ArgumentException>(() => new ThresholdSwitchStep(new("bad"), new("value"), [branches[0], branches[0]], [new(0, PhysicalUnit.Volt), new(1, PhysicalUnit.Ampere)]));
        Assert.Throws<ArgumentException>(() => new ThresholdSwitchStep(new("bad"), new("value"), Enumerable.Repeat(branches[0], 10_001), []));
        Assert.Throws<ArgumentException>(() => new ThresholdSwitchStep(new("bad"), new("value"), [null!], []));
    }
}

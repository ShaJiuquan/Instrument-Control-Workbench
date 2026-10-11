using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class BranchOnOutcomeStepTests
{
    [Fact]
    public void RequiresConditionAndThen()
    {
        var child = new DelayStep(new("child"), TimeSpan.Zero);
        Assert.Throws<ArgumentNullException>(() => new BranchOnOutcomeStep(new("branch"), null!, child));
        Assert.Throws<ArgumentNullException>(() => new BranchOnOutcomeStep(new("branch"), child, null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesChildrenAndOptionalElse(bool withElse)
    {
        var condition = new DelayStep(new("condition"), TimeSpan.Zero);
        var then = new DelayStep(new("then"), TimeSpan.Zero);
        var otherwise = withElse ? new DelayStep(new("else"), TimeSpan.Zero) : null;
        var branch = new BranchOnOutcomeStep(new("branch"), condition, then, otherwise);
        Assert.Same(condition, branch.Condition); Assert.Same(then, branch.Then); Assert.Same(otherwise, branch.Else);
    }
}

using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ControlStepTests
{
    [Theory]
    [InlineData(-0.001)]
    [InlineData(4294967.295)]
    public void TimeoutRejectsUnsupportedTimerDuration(double seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutStep(new("timeout"), TimeSpan.FromSeconds(seconds), Body()));
    [Theory]
    [InlineData(0)]
    [InlineData(4294967.294)]
    public void TimeoutPreservesZeroAndMaximumSupportedDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds); var body = Body();
        var step = new TimeoutStep(new("timeout"), duration, body);
        Assert.Equal(duration, step.Timeout); Assert.Same(body, step.Body);
    }
    [Fact]
    public void TimeoutRejectsNullBodyAndReportsTypedIdentityAndOriginalCancellation()
    {
        Assert.Throws<ArgumentNullException>(() => new TimeoutStep(new("timeout"), TimeSpan.Zero, null!));
        var cause = new OperationCanceledException(); var error = new PlanStepTimeoutException(new("timeout"), TimeSpan.FromSeconds(1), cause);
        Assert.Equal(new StepId("timeout"), error.Step); Assert.Equal(TimeSpan.FromSeconds(1), error.Timeout); Assert.Same(cause, error.InnerException);
    }
    private static ExperimentStep Body() => new DelayStep(new("body"), TimeSpan.Zero);
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000001)]
    public void InvalidRetryCountsCannotCreateUnboundedExecution(int attempts)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RetryStep(new("retry"), attempts, TimeSpan.Zero, Body()));
    [Theory]
    [InlineData(-0.001)]
    [InlineData(4294967.295)]
    public void UnsupportedRetryDelayIsRejected(double seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RetryStep(new("retry"), 2, TimeSpan.FromSeconds(seconds), Body()));
    [Fact]
    public void FallbackFreezesBranchesAndExposesDeterministicScopeIdentifiers()
    {
        var children = new List<ExperimentStep> { Body() }; var fallback = new FallbackStep(new("choose"), children); children.Clear();
        Assert.Single(fallback.Children); Assert.Equal(new ParameterId("choose.branch"), fallback.BranchColumn); Assert.Equal(new ParameterId("choose.total"), fallback.TotalColumn);
        var retry = new RetryStep(new("retry"), 2, TimeSpan.Zero, Body()); Assert.Equal(new ParameterId("retry.attempt"), retry.AttemptColumn);
    }
    [Fact]
    public void EmptyNullAndOversizedBranchesRejectAtTheDomainBoundary()
    {
        Assert.Throws<ArgumentException>(() => new FallbackStep(new("choose"), []));
        Assert.Throws<ArgumentException>(() => new FallbackStep(new("choose"), [null!]));
        Assert.Throws<ArgumentException>(() => new FallbackStep(new("choose"), Enumerable.Repeat(Body(), 10001)));
    }
    [Fact]
    public void MalformedResultsConditionsAndChildReferencesReject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResultDecoratorStep(new("effect"), (ResultEffect)99, Body()));
        Assert.Throws<ArgumentNullException>(() => new ResultDecoratorStep(new("effect"), ResultEffect.Invert, null!));
        Assert.Throws<ArgumentNullException>(() => new RetryStep(new("retry"), 1, TimeSpan.Zero, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConditionStep(new("condition"), new(new("x"), (ComparisonOperator)99, new ConstantValue(new(0, PhysicalUnit.Dimensionless)))));
        Assert.Throws<ArgumentNullException>(() => new ConditionStep(new("condition"), new(null!, ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless)))));
    }
    [Fact]
    public void TerminalFailureHasTypedStepIdentity()
    {
        var error = new PlanFailedException(new("root")); Assert.Equal(new StepId("root"), error.Step); Assert.Contains("workflow failure", error.Message);
    }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task CompletedScopeRetainsVariablesAndRestoresParentTokenForLaterSteps()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var value = new ParameterId("result");
        var body = new SequenceStep(new("body"), [new AssignValueStep(new("assign"), value, new ConstantValue(new(1, PhysicalUnit.Dimensionless))), b.ReadField("inside")]);
        var scope = new TimeoutStep(new("bounded"), TimeSpan.FromMilliseconds(500), body);
        var plan = new ExperimentPlan("Scoped timeout", new SequenceStep(new("root"), [scope, new DelayStep(new("after_delay"), TimeSpan.FromMilliseconds(650)),
            new ConditionStep(new("check"), new(value, ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless)))), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
        var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var fact)) facts.Add(fact);
        Assert.Contains(facts, f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == scope.Id && f.Outcome == StepOutcome.Success);
    }

    [Theory]
    [InlineData("force_success")]
    [InlineData("force_failure")]
    [InlineData("inverter")]
    [InlineData("fallback")]
    [InlineData("retry")]
    [InlineData("branch_outcome")]
    public async Task ExpiredScopeIsFaultNotWorkflowFailureAndControlsCannotSwallowOrRetryIt(string kind)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var timeout = new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(40), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)));
        var error = await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(b.Powered(Wrap(kind, timeout, b.ReadField("alternative"))), Token));
        Assert.Equal(timeout.Id, error.Step); Assert.Equal(timeout.Timeout, error.Timeout);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
        Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var fact)) facts.Add(fact);
        Assert.Single(facts, f => f.Kind == WorkbenchNotificationKind.StepStarted && f.Step == new StepId("wait"));
        Assert.DoesNotContain(facts, f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == timeout.Id);
    }

    [Fact]
    public async Task ZeroTimeoutExpiresBeforeChildIoAndIsNotAnUnlimitedWait()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(new("Zero", new TimeoutStep(new("deadline"), TimeSpan.Zero, b.ReadField("child"))), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        while (events.Reader.TryRead(out var fact)) Assert.False(fact.Kind == WorkbenchNotificationKind.StepStarted && fact.Step == new StepId("child"));
    }

    [Fact]
    public async Task ChildLogicalFailureKeepsItsOriginWithinUnexpiredScope()
    {
        var b = new Bench();
        var body = new SequenceStep(new("body"), [Check("failed", false), b.ReadField("unreachable")]);
        var error = await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(new("Logical", Seed(new TimeoutStep(new("deadline"), TimeSpan.FromSeconds(5), body))), Token));
        Assert.Equal(new StepId("failed"), error.Step); Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task OperatorAbortInsideScopeRemainsAbortedAndStopsPoweredSource()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); using var cancellation = new CancellationTokenSource();
        var run = b.Runs.StartPlanAsync(b.Powered(new TimeoutStep(new("deadline"), TimeSpan.FromSeconds(20), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)))), cancellation.Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Empty(b.Runs.LatestOutcome.Errors);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedScopesAttributeTheDeadlineToTheScopeThatExpired(bool outerFirst)
    {
        var b = new Bench();
        var inner = new TimeoutStep(new("inner"), TimeSpan.FromMilliseconds(outerFirst ? 5000 : 40), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)));
        var outer = new TimeoutStep(new("outer"), TimeSpan.FromMilliseconds(outerFirst ? 40 : 5000), inner);
        var error = await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(b.Powered(outer), Token));
        Assert.Equal(outerFirst ? outer.Id : inner.Id, error.Step);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task WallClockDeadlineContinuesWhilePausedAndNeverRestoresOutputAfterExpiry()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var run = b.Runs.StartPlanAsync(b.Powered(new TimeoutStep(new("deadline"), TimeSpan.FromSeconds(1), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)))), Token);
        await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); b.Runs.Pause();
        await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<PlanStepTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State); Assert.False(b.Runs.HasActiveRun);
        var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var fact)) facts.Add(fact);
        Assert.DoesNotContain(facts, f => f.Kind == WorkbenchNotificationKind.RunResumed);
    }

    [Fact]
    public async Task FailedHoldAfterDeadlineRetainsWholeGroupAndPreservesTimeoutCauseUntilRecovery()
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        var child = new SequenceStep(new("body"), [b.ReadField("before"), new DelayStep(new("wait"), TimeSpan.FromSeconds(30))]);
        var plan = b.Powered(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(100), child));
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Contains(b.Runs.LatestOutcome!.Errors, e => e is PlanStepTimeoutException);
        Assert.Contains(b.Runs.LatestOutcome.Errors, e => e.Message.Contains("hold refused", StringComparison.Ordinal));
        Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(field.Definition.Device.Id, DateTimeOffset.UtcNow));
        Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token));
        field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineCancelsPortButCannotReleaseOrStopAcrossAnUnfinishedCall(bool lateError)
    {
        var port = new DeadlineProbe(); var b = new Bench(new ArmDeadlineProbe(port), field: port);
        var run = b.Runs.StartPlanAsync(b.Powered(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(100), b.ReadField("value"))), Token);
        try
        {
            await port.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await port.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted); Assert.True(b.Runs.HasActiveRun); Assert.Equal(0, port.Holds);
            Assert.True(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow));
            Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.DisconnectAsync(port.Definition.Device.Id, Token));
            port.FailAfterRelease = lateError; port.Release.TrySetResult();
            var error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            if (lateError) Assert.Same(port.Failure, error); else Assert.IsType<PlanStepTimeoutException>(error);
            Assert.Equal(1, port.Holds); Assert.False(b.Runs.HasActiveRun);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        }
        finally { port.Release.TrySetResult(); if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    private sealed class ArmDeadlineProbe(DeadlineProbe port) : IRunResultListener
    {
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) { port.Armed = true; return Task.CompletedTask; }
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) => Task.CompletedTask;
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class DeadlineProbe : IAxisControllerPort
    {
        private readonly MockAxisController inner = MockEnvironmentCatalog.Field();
        public AxisControllerDefinition Definition => inner.Definition;
        public bool Armed { get; set; }
        public bool FailAfterRelease { get; set; }
        public int Holds { get; private set; }
        public IOException Failure { get; } = new("late instrument fault");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            if (Armed)
            {
                Armed = false; using var registration = token.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult(); await Release.Task;
                if (FailAfterRelease) throw Failure;
                return await inner.GetSnapshotAsync(Token);
            }
            return await inner.GetSnapshotAsync(token);
        }
        public Task SetTargetAsync(Quantity target, CancellationToken token) => inner.SetTargetAsync(target, token);
        public Task SetRateAsync(Quantity rate, CancellationToken token) => inner.SetRateAsync(rate, token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task HoldAsync(CancellationToken token) { Holds++; return inner.HoldAsync(token); }
    }
}

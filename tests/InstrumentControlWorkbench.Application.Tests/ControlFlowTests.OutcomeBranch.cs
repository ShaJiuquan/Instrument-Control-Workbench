using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task OutcomeBranchRunsConditionOnceAndOnlySelectedChild(bool success, bool withElse)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var condition = new SequenceStep(new("condition"), [b.ReadField("observed"), Check("predicate", success)]);
        var branch = new BranchOnOutcomeStep(new("branch"), condition, b.ReadField("then_read"), withElse ? b.ReadField("else_read") : null);
        await b.Runs.StartPlanAsync(b.Powered(Seed(branch)), Token);
        var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(success || withElse ? 2 : 1, rows.Count);
        Assert.True(rows[0].Values.ContainsKey(new("observed.value")));
        if (rows.Count == 2) Assert.True(rows[1].Values.ContainsKey(new(success ? "then_read.value" : "else_read.value")));
        var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var fact)) facts.Add(fact);
        Assert.Single(facts, f => f.Kind == WorkbenchNotificationKind.StepStarted && f.Step == condition.Id);
        Assert.Equal(success ? StepOutcome.Success : StepOutcome.Failure, facts.Single(f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == condition.Id).Outcome);
        Assert.Equal(StepOutcome.Success, facts.Single(f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == branch.Id).Outcome);
        Assert.DoesNotContain(facts, f => f.Kind == WorkbenchNotificationKind.StepStarted && f.Step == new StepId(success ? "else_read" : "then_read"));
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedBranchFailureKeepsOriginAndPreventsFollowingRows(bool success)
    {
        var b = new Bench(); var branch = new BranchOnOutcomeStep(new("branch"), Check("predicate", success), Check("then_fail", false), Check("else_fail", false));
        var error = await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(b.Powered(Seed(new SequenceStep(new("flow"), [branch, b.ReadField("unreachable")]))), Token));
        Assert.Equal(new StepId(success ? "then_fail" : "else_fail"), error.Step);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Theory]
    [InlineData("then")]
    [InlineData("forced_failure")]
    [InlineData("inverted")]
    public async Task CompletedConditionAcquisitionRemainsAvailableToSelectedBranch(string kind)
    {
        var b = new Bench(); ExperimentStep condition = b.ReadField("observed");
        if (kind != "then") condition = new ResultDecoratorStep(new("result"), kind == "forced_failure" ? ResultEffect.ForceFailure : ResultEffect.Invert, condition);
        var use = Equal("consume", "observed.value", new(0, PhysicalUnit.Tesla));
        var branch = new BranchOnOutcomeStep(new("branch"), condition, kind == "then" ? use : b.ReadField("unselected"), kind == "then" ? null : use);
        await b.Runs.StartPlanAsync(new("Condition variable", new SequenceStep(new("root"), [branch, b.ReadField("after")])), Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, b.Runs.LatestDataSet.Schema.ValidateRow);
    }

    [Fact]
    public async Task LogicalConditionFailureKeepsDeviceWritesAndParentVariables()
    {
        var b = new Bench(); var definition = b.Field.Definition;
        var condition = new SequenceStep(new("condition"), [Local("mutate", "value", Number(2)),
            new SetParameterStep(new("set"), new(definition.Device.Id, definition.Target.Id), new ConstantValue(new(0.01, PhysicalUnit.Tesla))), Check("predicate", false)]);
        var readTarget = new ReadStep(new("target"), [new(new("actual.target"), new(definition.Device.Id, definition.Target.Id))]);
        var branch = new BranchOnOutcomeStep(new("branch"), condition, b.ReadField("unselected"), new SequenceStep(new("otherwise"), [Equal("mutation_kept", "value", Number(2)), readTarget]));
        await b.Runs.StartPlanAsync(new("State", Seed(new SequenceStep(new("root"), [Local("seed_value", "value", Number(1)), branch]))), Token);
        Assert.Equal(new Quantity(0.01, PhysicalUnit.Tesla), b.Runs.LatestDataSet!.Rows.Single().Values[new("actual.target")]);
    }

    [Fact]
    public async Task ListenerFaultInConditionDoesNotSelectEitherBranch()
    {
        var listener = new RefusingListener(); var b = new Bench(listener);
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(new BranchOnOutcomeStep(new("branch"), b.ReadSource("condition"), b.ReadField("then"), b.ReadField("else"))), Token));
        Assert.NotNull(error); Assert.Contains("listener refused", error.Message); Assert.Equal(1, listener.Rows);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbortInsideSelectedBranchCannotRunAnotherBranch(bool success)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var wait = new DelayStep(new("wait"), TimeSpan.FromSeconds(30)); var alternative = b.ReadField("unselected");
        var branch = new BranchOnOutcomeStep(new("branch"), Check("predicate", success), success ? wait : alternative, success ? alternative : wait);
        var run = b.Runs.StartPlanAsync(b.Powered(Seed(branch)), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, wait.Id); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task PauseInsideConditionResumesOnceBeforeSelectingBranch()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var condition = new SequenceStep(new("condition"), [new DelayStep(new("wait"), TimeSpan.FromMilliseconds(400)), b.ReadField("observed")]);
        var run = b.Runs.StartPlanAsync(b.Powered(new BranchOnOutcomeStep(new("branch"), condition, b.ReadField("then"), b.ReadField("else"))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            b.Runs.Resume(); await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count);
            Assert.True(b.Runs.LatestDataSet.Rows[1].Values.ContainsKey(new("then.value"))); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task BranchCompletionCannotReleaseGroupAfterFailedHold()
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        var branch = new BranchOnOutcomeStep(new("branch"), Check("predicate", false), b.ReadField("unselected"), b.ReadField("selected"));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Powered(Seed(branch)), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.True(b.Leases.IsLeased(field.Definition.Device.Id, DateTimeOffset.UtcNow));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Powered(Seed(branch)), Token));
        field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }
}

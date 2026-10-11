using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static MutateValueStep Mutate(string id, string variable, VariableMutation action, double value) => new(new(id), new(variable), action, Number(value));
    private static ValueCondition Below(string variable, double value) => new(new(variable), ComparisonOperator.Less, new ConstantValue(Number(value)));

    [Fact]
    public async Task FailureMutationRejectsUnitsPossiblyDefinedByPartiallyCompletedBody()
    {
        var b = new Bench(); var body = new SequenceStep(new("body"), [Local("define", "late", new(1, PhysicalUnit.Tesla)), Check("failed", false)]);
        var hook = new OutcomeMutationStep(new("hook"), body, failure: Mutate("invalid", "late", VariableMutation.Set, 0));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Powered(Seed(hook)))); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData(VariableMutation.Set)]
    [InlineData(VariableMutation.Add)]
    [InlineData(VariableMutation.Subtract)]
    public async Task MutationCannotChangeExistingPhysicalUnitsBeforeIo(VariableMutation action)
    {
        var b = new Bench(); var plan = new ExperimentPlan("Units", new SequenceStep(new("root"), [Local("seed", "field", new(0.01, PhysicalUnit.Tesla)), Mutate("bad", "field", action, 1), b.ReadField("after")]));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan)); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task ScalarMutationPreservesPhysicalUnitsWithoutWritingDevice()
    {
        var b = new Bench(); var plan = new ExperimentPlan("Scalar", new SequenceStep(new("root"), [Local("seed", "field", new(0.01, PhysicalUnit.Tesla)), Mutate("double", "field", VariableMutation.Multiply, 2), Equal("verify", "field", new(0.02, PhysicalUnit.Tesla)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(new Quantity(0, PhysicalUnit.Tesla), b.Runs.LatestDataSet!.Rows.Single().Values[new("after.value")]);
    }

    [Theory]
    [InlineData(VariableMutation.Set, true, 4)]
    [InlineData(VariableMutation.Add, true, 6)]
    [InlineData(VariableMutation.Subtract, true, -2)]
    [InlineData(VariableMutation.Multiply, true, 8)]
    [InlineData(VariableMutation.Set, false, 4)]
    [InlineData(VariableMutation.Add, false, 4)]
    [InlineData(VariableMutation.Subtract, false, -4)]
    [InlineData(VariableMutation.Multiply, false, 0)]
    public async Task VariableMutationsPreserveExpectedArithmeticAndMissingZeroBase(VariableMutation action, bool seeded, double expected)
    {
        var b = new Bench(); var steps = new List<ExperimentStep>(); if (seeded) steps.Add(Local("seed", "value", Number(2)));
        steps.Add(Mutate("change", "value", action, 4)); steps.Add(Equal("verify", "value", Number(expected))); steps.Add(b.ReadField("after"));
        await b.Runs.StartPlanAsync(new("Arithmetic", new SequenceStep(new("root"), steps)), Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task WhileReevaluatesMutatedValueAndRestoresRowAxesAfterThreeIterations()
    {
        var b = new Bench(); var loop = new WhileStep(new("loop"), Below("count", 3), new SequenceStep(new("body"), [b.ReadField("observed"), Mutate("advance", "count", VariableMutation.Add, 1)]));
        var plan = new ExperimentPlan("Three iterations", new SequenceStep(new("root"), [Local("seed", "count", Number(0)), loop, Equal("final", "count", Number(3)), b.ReadField("after")]), maximumStepExecutions: 20);
        var preview = b.Runs.PreviewPlan(plan); Assert.True(preview.HasDynamicLoops); Assert.Equal(20, preview.MaximumStepExecutions);
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(4, rows.Count); Assert.Equal(new[] { 1.0, 2, 3 }, rows.Take(3).Select(r => r.Values[loop.IterationColumn].Value));
        Assert.False(rows[^1].Values.ContainsKey(loop.IterationColumn)); Assert.All(rows, b.Runs.LatestDataSet.Schema.ValidateRow);
    }

    [Fact]
    public async Task InitiallyFalseWhileDoesNotRunBodyOrGuaranteeItsNewVariables()
    {
        var b = new Bench(); var loop = new WhileStep(new("loop"), Below("count", 0), Local("define", "new.value", Number(1)));
        var steps = new List<ExperimentStep> { Local("seed", "count", Number(0)), loop, b.ReadField("after") };
        await b.Runs.StartPlanAsync(new("False", new SequenceStep(new("root"), steps)), Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.False(b.Runs.LatestDataSet.Rows[0].Values.ContainsKey(loop.IterationColumn));
        steps.Insert(2, Equal("unsafe", "new.value", Number(1))); Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Undefined", new SequenceStep(new("root"), steps))));
    }

    [Fact]
    public async Task BodyLogicalFailureStopsLoopAndKeepsFailureOriginAndRead()
    {
        var b = new Bench(); var loop = new WhileStep(new("loop"), Below("count", 3), new SequenceStep(new("body"), [b.ReadField("observed"), Check("failed", false), Mutate("unreachable", "count", VariableMutation.Add, 1)]));
        var error = await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(b.Powered(Seed(new SequenceStep(new("flow"), [Local("count", "count", Number(0)), loop]))), Token));
        Assert.Equal(new StepId("failed"), error.Step); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task BudgetExhaustionIsTypedFaultAndForceSuccessCannotHideIt()
    {
        var b = new Bench(); var loop = new WhileStep(new("loop"), Below("count", 10), new SequenceStep(new("body"), [b.ReadField("observed"), Mutate("advance", "count", VariableMutation.Add, 1)]));
        var powered = b.Powered(new SequenceStep(new("flow"), [Local("seed", "count", Number(0)), new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, loop)]));
        var plan = new ExperimentPlan(powered.Name, powered.Root, powered.Preparations, maximumStepExecutions: 9);
        var error = await Assert.ThrowsAsync<PlanExecutionBudgetException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(9, error.Maximum); Assert.Equal(new StepId("body"), error.Step);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task MutationOverflowRetainsRowAndDoesNotInvokeFailureHook()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var hook = new OutcomeMutationStep(new("hook"), new SequenceStep(new("body"), [b.ReadField("observed"), Mutate("overflow", "value", VariableMutation.Add, double.MaxValue)]), failure: Mutate("forbidden", "value", VariableMutation.Set, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("flow"), [Local("seed", "value", Number(double.MaxValue)), hook])), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        while (events.Reader.TryRead(out var fact)) Assert.False(fact.Kind == WorkbenchNotificationKind.StepStarted && fact.Step == new StepId("forbidden"));
    }

    [Fact]
    public async Task PauseWithinWhileKeepsIterationAndAbortStopsPoweredGroup()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var loop = new WhileStep(new("loop"), Below("count", 3), new SequenceStep(new("body"), [b.ReadField("before"), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), Mutate("advance", "count", VariableMutation.Add, 1)]));
        var run = b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("flow"), [Local("seed", "count", Number(0)), loop])), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            Assert.Equal(1, b.Runs.LatestDataSet!.Rows.Single().Values[loop.IterationColumn].Value);
            b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(b.Runs.LatestDataSet.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task CompletedWhileCannotReleaseGroupAfterFailedFinalHold()
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        var loop = new WhileStep(new("loop"), Below("count", 1), new SequenceStep(new("body"), [b.ReadField("observed"), Mutate("advance", "count", VariableMutation.Add, 1)]));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("flow"), [Local("seed", "count", Number(0)), loop])), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }
}

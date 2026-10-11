using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Theory]
    [InlineData(-1, 3)]
    [InlineData(0, 1)]
    [InlineData(4.9999999999, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    public async Task ThresholdSwitchExecutesOnlyTheSelectedBranchAndPreservesSourceData(double value, int branch)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var choice = new ThresholdSwitchStep(new("choose"), new("selector"), Enumerable.Range(0, 4).Select(i => b.ReadField("branch" + i)),
            [new(10, PhysicalUnit.Tesla), new(0, PhysicalUnit.Tesla), new(5, PhysicalUnit.Tesla)]);
        var plan = new ExperimentPlan("switch", new SequenceStep(new("root"), [Local("seed", "selector", new(value, PhysicalUnit.Tesla)), choice]));
        await b.Runs.StartPlanAsync(b.Workspace.Validate(b.Workspace.Inspect(plan).Text).Plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(row.Values.ContainsKey(new("branch" + branch + ".value")));
        Assert.All(Enumerable.Range(0, 4).Where(i => i != branch), i => Assert.False(row.Values.ContainsKey(new("branch" + i + ".value"))));
        var steps = new List<StepId>(); while (events.Reader.TryRead(out var fact)) if (fact.Kind == WorkbenchNotificationKind.StepStarted && fact.Step is { } id) steps.Add(id);
        Assert.Single(steps, id => id.Value.StartsWith("branch", StringComparison.Ordinal)); Assert.Contains(choice.Id, steps);
        Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("boolean")]
    [InlineData("text")]
    [InlineData("unit")]
    [InlineData("branch_only")]
    public async Task ThresholdSwitchSelectorOrPathAvailabilityRejectsBeforeDeviceAdmission(string invalid)
    {
        var b = new Bench(); PlanValue value = invalid switch { "boolean" => new BooleanValue(true), "text" => new TextValue("0"), _ => new ConstantValue(Number(0)) };
        ExperimentStep[] branches = [Local("write", "result", Number(1)), b.ReadField("other")];
        var choice = new ThresholdSwitchStep(new("choose"), new("selector"), branches, [new(0, invalid == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Dimensionless)]);
        var steps = new List<ExperimentStep>(); if (invalid != "missing") steps.Add(new AssignValueStep(new("seed"), new("selector"), value)); steps.Add(choice);
        if (invalid == "branch_only") steps.Add(new AssignValueStep(new("use"), new("after"), new VariableValue(new("result"))));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Powered(new SequenceStep(new("flow"), steps))));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ThresholdSwitchCommonBranchVariablesAndWorstCaseBudgetFollowOneSelectedPath()
    {
        var b = new Bench(); var branches = Enumerable.Range(0, 3).Select(i => (ExperimentStep)new SequenceStep(new("branch" + i),
            [Local("write" + i, "result", Number(i)), new RepeatStep(new("repeat" + i), 2, new DelayStep(new("delay" + i), TimeSpan.Zero))])).ToArray();
        var choice = new ThresholdSwitchStep(new("choose"), new("selector"), branches, [Number(0), Number(2)]);
        var plan = new ExperimentPlan("common outputs", new SequenceStep(new("root"), [Local("seed", "selector", Number(3)), choice,
            new AssignValueStep(new("use"), new("after"), new VariableValue(new("result")))]), maximumStepExecutions: 9);
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(new BlackboardQuantity(Number(1)), b.Runs.LatestBlackboard![new("after")].Value);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }

    [Fact]
    public async Task ThresholdSwitchEmptyIsANoOpAndSelectedExpectedFailureCanDriveAnOutcomeBranch()
    {
        var b = new Bench(); var choice = new ThresholdSwitchStep(new("choose"), Flag, [Check("failed", false), b.ReadField("unused")], [Number(0)]);
        var body = new SequenceStep(new("flow"), [new ThresholdSwitchStep(new("empty"), new("never_bound"), [], []),
            new BranchOnOutcomeStep(new("outcome"), choice, b.ReadField("then_unused"), b.ReadField("failure_read"))]);
        await b.Runs.StartPlanAsync(b.Powered(Seed(body)), Token);
        Assert.True(Assert.Single(b.Runs.LatestDataSet!.Rows).Values.ContainsKey(new("failure_read.value")));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ThresholdSwitchAbortSelectedDelayDrainsAndStopsTheWholeRun()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var wait = new DelayStep(new("selected_wait"), TimeSpan.FromSeconds(30));
        var choice = new ThresholdSwitchStep(new("choose"), Flag, [wait, b.ReadField("unselected")], [Number(0)]);
        var run = b.Runs.StartPlanAsync(b.Powered(Seed(choice)), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, wait.Id); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Empty(b.Runs.LatestDataSet!.Rows);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
}

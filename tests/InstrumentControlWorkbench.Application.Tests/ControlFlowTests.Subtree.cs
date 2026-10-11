using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static AssignValueStep Local(string id, string variable, Quantity value) => new(new(id), new(variable), new ConstantValue(value));
    private static ConditionStep Equal(string id, string variable, Quantity value) => new(new(id), new(new(variable), ComparisonOperator.Equal, new ConstantValue(value)));
    private static Quantity Number(double value) => new(value, PhysicalUnit.Dimensionless);

    [Fact]
    public async Task SubtreeInheritsParentValuesAndExportsOnlyExplicitResults()
    {
        var b = new Bench();
        var scope = new SubtreeStep(new("scope"), [Equal("inherited", "value", Number(1)), Local("change", "value", Number(2)), b.ReadField("observed")],
            outputs: [new(new("value"), new("exported"))]);
        var plan = new ExperimentPlan("Scope", new SequenceStep(new("root"), [Local("seed", "value", Number(1)), scope,
            Equal("parent_kept", "value", Number(1)), Equal("selected_output", "exported", Number(2)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task InputAndOutputSwapsReadCompleteSnapshotsRatherThanMappingOrder()
    {
        var b = new Bench();
        var scope = new SubtreeStep(new("scope"), [Equal("local_a", "a", Number(2)), Equal("local_b", "b", Number(1)),
            Local("write_a", "a", Number(3)), Local("write_b", "b", Number(4))],
            [new(new("a"), new("b")), new(new("b"), new("a"))], [new(new("a"), new("b")), new(new("b"), new("a"))]);
        var plan = new ExperimentPlan("Swap", new SequenceStep(new("root"), [Local("seed_a", "a", Number(1)), Local("seed_b", "b", Number(2)), scope,
            Equal("output_a", "a", Number(4)), Equal("output_b", "b", Number(3)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task ExplicitInputCanShadowInheritedUnitsLocallyWithoutChangingParentUnit()
    {
        var b = new Bench();
        var scope = new SubtreeStep(new("scope"), [Equal("shadow", "value", new(2, PhysicalUnit.Ampere))],
            [new(new("current"), new("value"))], [new(new("value"), new("selected_current"))]);
        var plan = new ExperimentPlan("Local units", new SequenceStep(new("root"), [Local("seed_v", "value", new(1, PhysicalUnit.Volt)), Local("seed_i", "current", new(2, PhysicalUnit.Ampere)), scope,
            Equal("parent_v", "value", new(1, PhysicalUnit.Volt)), Equal("exported_i", "selected_current", new(2, PhysicalUnit.Ampere)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task NestedSubtreesRestoreNearestScopeAndRequireExplicitExportAtEachBoundary()
    {
        var b = new Bench();
        var inner = new SubtreeStep(new("inner"), [Equal("inner_input", "alias", Number(2)), Local("inner_write", "value", Number(3))],
            [new(new("value"), new("alias"))], [new(new("value"), new("inner_result"))]);
        var outer = new SubtreeStep(new("outer"), [Local("outer_write", "value", Number(2)), inner,
            Equal("outer_value", "value", Number(2)), Equal("inner_export", "inner_result", Number(3))], outputs: [new(new("inner_result"), new("final"))]);
        var plan = new ExperimentPlan("Nested scopes", new SequenceStep(new("root"), [Local("seed", "value", Number(1)), outer,
            Equal("parent", "value", Number(1)), Equal("final_export", "final", Number(3)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task EmptySubtreeMayExportInheritedValueWithoutInventingAnObservation()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"), [], outputs: [new(new("value"), new("selected"))]);
        await b.Runs.StartPlanAsync(new("Empty scope", new SequenceStep(new("root"), [Local("seed", "value", Number(1)), scope,
            Equal("selected", "selected", Number(1)), b.ReadField("after")])), Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task FailedScopeDoesNotExportMutationAndFallbackKeepsItsCommittedRead()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"), [Local("mutate", "value", Number(2)), b.ReadField("failed_read"), Equal("failure", "value", Number(9))],
            outputs: [new(new("value"), new("value"))]);
        var fallback = new FallbackStep(new("choose"), [scope, new SequenceStep(new("alternative"), [Equal("parent", "value", Number(1)), b.ReadField("alternative_read")])]);
        await b.Runs.StartPlanAsync(new("Failure isolation", new SequenceStep(new("root"), [Local("seed", "value", Number(1)), fallback,
            Equal("still_parent", "value", Number(1)), b.ReadField("after")])), Token);
        var data = b.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count);
        Assert.Equal(new[] { 1.0, 2.0 }, data.Rows.Take(2).Select(r => r.Values[fallback.BranchColumn].Value));
        Assert.False(data.Rows[^1].Values.ContainsKey(fallback.BranchColumn));
    }

    [Fact]
    public async Task ForceSuccessCannotLeakWritesOrOutputsFromAnEarlyFailedScope()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"),
            [Local("mutate", "value", Number(2)), b.ReadField("inside"), Equal("failed", "value", Number(9))],
            outputs: [new(new("value"), new("value"))]);
        var plan = new ExperimentPlan("Failed scope isolation", new SequenceStep(new("root"),
            [Local("seed", "value", Number(1)), new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, scope),
                Equal("parent", "value", Number(1)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }

    [Fact]
    public async Task ScopeIsolationDoesNotUndoRealDeviceWritesOrDiscardReadbackRows()
    {
        var b = new Bench(); var device = b.Field.Definition.Device.Id; var target = b.Field.Definition.Target.Id;
        var scope = new SubtreeStep(new("scope"), [new SetParameterStep(new("set"), new(device, target), new ConstantValue(new(0.01, PhysicalUnit.Tesla)))]);
        var read = new ReadStep(new("after"), [new(new("actual.target"), new(device, target))]);
        await b.Runs.StartPlanAsync(new("Device state", new SequenceStep(new("root"), [scope, read])), Token);
        Assert.Equal(new Quantity(0.01, PhysicalUnit.Tesla), b.Runs.LatestDataSet!.Rows.Single().Values[new("actual.target")]);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Moving); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PauseWithinScopeKeepsLocalsUntilResumeThenRestoresParentAndExports()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var scope = new SubtreeStep(new("scope"), [Local("mutate", "value", Number(2)), new DelayStep(new("wait"), TimeSpan.FromMilliseconds(400)), b.ReadField("inside")],
            outputs: [new(new("value"), new("selected"))]);
        var run = b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("flow"), [Local("seed", "value", Number(1)), scope,
            Equal("parent", "value", Number(1)), Equal("output", "selected", Number(2))])), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); b.Runs.Pause();
            await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
            b.Runs.Resume(); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(b.Runs.LatestDataSet.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task ScopeInsideTimeoutCannotExportOrTransformExpiryAndStillStopsGroup()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"), [Local("mutate", "value", Number(2)), new DelayStep(new("wait"), TimeSpan.FromSeconds(30))],
            outputs: [new(new("value"), new("value"))]);
        var plan = b.Powered(new SequenceStep(new("flow"), [Local("seed", "value", Number(1)), new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(40), scope)]));
        Assert.Equal(new StepId("deadline"), (await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(plan, Token))).Step);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task SuccessfulScopeCannotHideFailedFinalHoldOrReleaseWholeGroup()
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Powered(new SubtreeStep(new("scope"), [b.ReadField("inside")])), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }
}

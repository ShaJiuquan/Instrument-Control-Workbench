using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TemperatureControlExecutionTests
{
    [Theory]
    [InlineData("wait_temp", "stable")]
    [InlineData("wait_temp_stable", "stable")]
    [InlineData("wait_temperature", "stable")]
    [InlineData("wait_temp_state", "reached")]
    [InlineData("wait_temp_reached", "reached")]
    [InlineData("wait", "reached")]
    public async Task TemperatureStateAliasesObserveExistingControllerWithoutSettingTarget(string name, string requested)
    {
        var b = new Bench(useStateClock: true);
        var parameters = new JsonObject { ["wait_for"] = requested, ["timeout_s"] = 1, ["poll_ms"] = 1 };
        if (name == "wait") parameters["wait_kind"] = "wait_temp";
        var command = new JsonObject { ["type"] = "command", ["name"] = name, ["params"] = parameters };
        var waitPlan = new PpmsExperimentPlanImporter().Parse(command.ToJsonString(), b.Bindings);
        var timeout = Assert.IsType<TimeoutStep>(waitPlan.Root); var wait = Assert.IsType<WaitTemperatureStateStep>(timeout.Body);
        Assert.Equal(requested == "stable" ? TemperatureWaitMode.Stable : TemperatureWaitMode.Reached, wait.Mode);
        Assert.Equal(TimeSpan.FromMilliseconds(100), wait.PollInterval);
        var plan = new ExperimentPlan("State wait", new SequenceStep(new("root"),
            [new TemperatureStabilityStep(new("settings"), b.Id, new(stableFor: TimeSpan.Zero)), waitPlan.Root, b.Read()]));
        var codec = new JsonExperimentPlanDocument(); await b.Runs.StartPlanAsync(codec.Parse(codec.Serialize(plan)), Token);
        Assert.Equal(300, Assert.Single(b.Runs.LatestDataSet!.Rows).Values[new("temperature.actual")].Value);
        Assert.Empty(b.Port.HardwareRampTargets); Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task StableWaitDoesNotAcceptReachedAndAdvancesOnlyWithFreshStateEvidence()
    {
        var b = new Bench(useStateClock: true); using var events = b.Hub.Subscribe();
        var step = new WaitTemperatureStateStep(new("state.wait"), b.Id, TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(20));
        var run = b.Runs.StartPlanAsync(new("Fresh stable state", new TimeoutStep(new("deadline"), TimeSpan.FromSeconds(2), step)), Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var fact = await events.Reader.ReadAsync(deadline.Token);
                if (fact.Step == step.Id && fact.Kind == WorkbenchNotificationKind.LogAppended && fact.Message.Contains("Reached", StringComparison.Ordinal)) break;
            }
            Assert.False(run.IsCompleted); b.Clock.Advance(TimeSpan.FromSeconds(5)); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task ReachedAliasUsesItsDefaultWithoutAuthoringWaitFor()
    {
        var b = new Bench(); var json = new JsonObject { ["type"] = "command", ["name"] = "wait_temp_reached", ["params"] = new JsonObject { ["timeout_s"] = 0 } };
        var plan = new PpmsExperimentPlanImporter().Parse(json.ToJsonString(), b.Bindings); Assert.Equal(TemperatureWaitMode.Reached, Assert.IsType<WaitTemperatureStateStep>(plan.Root).Mode);
        await b.Runs.StartPlanAsync(plan, Token); Assert.False(b.Runs.HasActiveRun); Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }
    [Fact]
    public async Task StableStateZeroTimeoutHasNoDeadlineAndCanBeAborted()
    {
        var b = new Bench(useStateClock: true); using var events = b.Hub.Subscribe();
        var json = new JsonObject { ["type"] = "command", ["name"] = "wait_temp", ["params"] = new JsonObject { ["timeout_s"] = 0, ["poll_ms"] = 100 } };
        var plan = new PpmsExperimentPlanImporter().Parse(json.ToJsonString(), b.Bindings); Assert.IsType<WaitTemperatureStateStep>(plan.Root);
        var run = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); b.Runs.Abort(); await run; Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State);
            Assert.False(b.Runs.HasActiveRun); Assert.True(b.Port.Holds > 0); Assert.Empty(b.Runs.LatestDataSet.Rows);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TemperatureControlExecutionTests
{
    [Theory]
    [InlineData("unknown_state")]
    [InlineData("duplicate_state")]
    [InlineData("target_override")]
    [InlineData("negative_timeout")]
    [InlineData("bad_poll")]
    public async Task StateWaitMalformedParametersRejectBeforeIo(string damage)
    {
        var b = new Bench(); var p = new JsonObject();
        switch (damage)
        {
            case "unknown_state": p["wait_for"] = "cooling"; break;
            case "duplicate_state": p["wait_for"] = "stable"; p["state"] = "stable"; break;
            case "target_override": p["target_k"] = 300; break;
            case "negative_timeout": p["timeout_s"] = -1; break;
            case "bad_poll": p["poll_ms"] = 0; break;
        }
        var json = new JsonObject { ["type"] = "command", ["name"] = "wait_temp", ["params"] = p, ["source_line"] = 3 };
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(json.ToJsonString(), b.Bindings));
        Assert.Contains("source line 3", error.Message); Assert.False((await b.Port.Mock.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task AngleWaitCannotUseTemperatureControllerAndTemperatureDefaultDeadlineIsExplicit()
    {
        var b = new Bench(); var native = new ExperimentPlan("Wrong controller role", new WaitAngleStep(new("wait"), b.Id, new(0.1, Core.Parameters.PhysicalUnit.Degree), TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(native, Token)); Assert.False((await b.Port.Mock.GetSnapshotAsync(Token)).Connected);
        var json = new JsonObject { ["type"] = "command", ["name"] = "wait_temp", ["params"] = new JsonObject() };
        var plan = new PpmsExperimentPlanImporter().Parse(json.ToJsonString(), b.Bindings); Assert.Equal(TimeSpan.FromSeconds(3600), Assert.IsType<TimeoutStep>(plan.Root).Timeout);
    }
    [Fact]
    public async Task StateWaitFiniteDeadlineIsNotConvertedToOrdinaryFailureOrRetried()
    {
        var b = new Bench(useStateClock: true); var child = new TimeoutStep(new("state.deadline"), TimeSpan.FromMilliseconds(50),
            new WaitTemperatureStateStep(new("state.wait"), b.Id, TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(20)));
        var plan = new ExperimentPlan("Do not mask state deadline", new ResultDecoratorStep(new("mask"), ResultEffect.ForceSuccess,
            new RetryStep(new("retry"), 3, TimeSpan.Zero, child)));
        using var events = b.Hub.Subscribe(); await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(plan, Token));
        var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var item)) facts.Add(item);
        Assert.Single(facts, f => f.Step == new StepId("state.wait") && f.Kind == WorkbenchNotificationKind.StepStarted);
        Assert.False(b.Runs.HasActiveRun); Assert.True(b.Port.Holds > 0);
    }
}

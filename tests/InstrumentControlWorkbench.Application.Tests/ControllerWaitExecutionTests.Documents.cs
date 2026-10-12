using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControllerWaitExecutionTests
{
    [Theory]
    [InlineData("negative_timeout")]
    [InlineData("null_timeout")]
    [InlineData("text_timeout")]
    [InlineData("duplicate_timeout")]
    [InlineData("zero_poll")]
    [InlineData("fractional_poll")]
    [InlineData("oversized_poll")]
    [InlineData("negative_tolerance")]
    [InlineData("duplicate_tolerance")]
    [InlineData("duplicate_target")]
    [InlineData("target_bound")]
    [InlineData("unknown_field")]
    [InlineData("wrong_wait_kind")]
    [InlineData("mixed_time_fields")]
    public async Task MalformedWaitContractRejectsWithSourceLineBeforeControllerConnection(string damage)
    {
        var b = new Bench(); var p = new JsonObject(); var name = "wait_angle";
        switch (damage)
        {
            case "negative_timeout": p["timeout_s"] = -1; break;
            case "null_timeout": p["timeout_s"] = null; break;
            case "text_timeout": p["timeout_s"] = "1"; break;
            case "duplicate_timeout": p["timeout_s"] = 1; p["timeout"] = 1; break;
            case "zero_poll": p["poll_ms"] = 0; break;
            case "fractional_poll": p["poll_ms"] = 1.5; break;
            case "oversized_poll": p["poll_ms"] = (double)uint.MaxValue; break;
            case "negative_tolerance": p["tolerance_deg"] = -0.1; break;
            case "duplicate_tolerance": p["tolerance_deg"] = 0.1; p["tolerance"] = 0.1; break;
            case "duplicate_target": p["target_deg"] = 0; p["target"] = 0; break;
            case "target_bound": p["target_deg"] = 361; break;
            case "unknown_field": p["fake_state"] = "stable"; break;
            case "wrong_wait_kind": name = "wait"; p["wait_kind"] = "unknown"; break;
            case "mixed_time_fields": name = "wait"; p["wait_kind"] = "wait_angle"; p["seconds"] = 1; break;
        }
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(Node(name, p), b.Bindings));
        Assert.Contains("source line 7", error.Message); Assert.False((await b.Angle.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("mode")]
    [InlineData("unknown")]
    [InlineData("null_target")]
    [InlineData("poll")]
    [InlineData("tolerance")]
    public void NativeWaitFieldsAreStrictAndDoNotInventTargets(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var plan = new ExperimentPlan("Angle wait", new WaitAngleStep(new("wait"), new("angle"), new(0.1, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(100)));
        var json = JsonNode.Parse(codec.Serialize(plan))!; var root = json["root"]!;
        switch (damage)
        {
            case "mode": json = JsonNode.Parse(codec.Serialize(new("State wait", new WaitTemperatureStateStep(new("wait"), new("temp"), TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(100)))))!; json["root"]!["mode"] = "unknown"; break;
            case "unknown": root["timeout_s"] = 0; break;
            case "null_target": root["target"] = null; break;
            case "poll": root["pollSeconds"] = 0; break;
            case "tolerance": root["tolerance"]!["unit"] = "Kelvin"; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(json.ToJsonString()));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("boolean")]
    [InlineData("unit")]
    [InlineData("range")]
    public async Task NativeVariableTargetIsCheckedBeforeAnyIo(string damage)
    {
        var b = new Bench(); var body = new WaitAngleStep(new("wait"), b.Id, new(0.1, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(100), new VariableValue(new("target")));
        var value = damage == "boolean" ? (PlanValue)new BooleanValue(true) : new ConstantValue(new(damage == "range" ? 999 : 0, damage == "unit" ? PhysicalUnit.Tesla : PhysicalUnit.Degree));
        var root = damage == "missing" ? (ExperimentStep)body : new SequenceStep(new("root"), [new AssignValueStep(new("assign"), new("target"), value), body]);
        if (damage == "range")
        {
            // Dynamic values are unit-preflighted, then range-checked at execution.
            await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Runs.StartPlanAsync(new("Dynamic target", root), Token));
            Assert.True(b.Angle.Holds > 0); Assert.False(b.Runs.HasActiveRun);
        }
        else
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Runs.StartPlanAsync(new("Invalid target", root), Token)); Assert.False((await b.Angle.GetSnapshotAsync(Token)).Connected);
        }
    }
    [Fact]
    public async Task WaitAndActualAngleRowsReopenAndExecuteThroughArchiveAndCsv()
    {
        var folder = Path.Combine(Path.GetTempPath(), "angle-state-wait-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive);
            var plan = new ExperimentPlan("Measured after current target", new SequenceStep(new("root"),
                [new WaitAngleStep(new("wait"), b.Id, new(0.1, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(100)),
                 new ReadStep(new("read"), [new(new("angle.actual"), new(b.Id, b.Angle.Definition.Readback.Id))])]));
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); await b.Runs.StartPlanAsync(await codec.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            Assert.Equal(0, Assert.Single(b.Runs.LatestDataSet!.Rows).Values[new("angle.actual")].Value);
            await using var freshArchive = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await freshArchive.LoadAsync(b.Runs.LatestDataSet.Id, Token);
            Assert.NotNull(saved.DataSet.Blackboard); await freshArchive.ExportCsvAsync(saved.DataSet.Id, Path.Combine(folder, "angle.csv"), Token); Assert.Contains("angle.actual", await File.ReadAllTextAsync(Path.Combine(folder, "angle.csv")));
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Single(fresh.Runs.LatestDataSet!.Rows); Assert.Equal(0, fresh.Angle.TargetWrites);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineWaitsForNoncooperativeSnapshotBeforeHoldAndAbortHasPrecedence(bool abort)
    {
        var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = b.Hub.Subscribe(); var waiting = new WaitAngleStep(new("angle.wait"), b.Id, new(0.1, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(20));
        var run = b.Runs.StartPlanAsync(new("Drain late observation", new SequenceStep(new("root"),
            [new DelayStep(new("marker"), TimeSpan.FromMilliseconds(100)), new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(50), waiting)])), Token);
        try
        {
            await Until(events, Core.Notifications.WorkbenchNotificationKind.StepStarted);
            b.Angle.BeforeSnapshot = async token => { entered.TrySetResult(); await release.Task; };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var holds = b.Angle.Holds;
            await Task.Delay(100); if (abort) b.Runs.Abort(); Assert.False(run.IsCompleted); Assert.Equal(holds, b.Angle.Holds); Assert.True(b.Leases.IsLeased(b.Id, DateTimeOffset.UtcNow.AddYears(1)));
            b.Angle.BeforeSnapshot = null; release.TrySetResult();
            if (abort) { await run; Assert.Equal(Core.Measurements.MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); }
            else await Assert.ThrowsAsync<PlanStepTimeoutException>(() => run);
            Assert.True(b.Angle.Holds > holds); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Angle.BeforeSnapshot = null; release.TrySetResult(); if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
}

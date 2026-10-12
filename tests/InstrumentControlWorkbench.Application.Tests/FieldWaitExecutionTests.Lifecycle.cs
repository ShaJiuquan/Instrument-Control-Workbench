using System.Diagnostics;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class FieldWaitExecutionTests
{
    [Fact]
    public async Task PauseDoesNotCountUnobservedTimeAsFieldStability()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stableFor = TimeSpan.FromMilliseconds(300);
        var run = b.Runs.StartPlanAsync(b.Gated(new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), stableFor, TimeSpan.FromMilliseconds(20))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); b.Field.Observe = s => { entered.TrySetResult(); return s; };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(100); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            await Task.Delay(350); Assert.False(run.IsCompleted); var resumed = Stopwatch.GetTimestamp(); b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed);
            await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(Stopwatch.GetElapsedTime(resumed) >= stableFor); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Field.Observe = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutNoncooperativeReadIsDrainedBeforeHoldAndAbortKeepsPriority(bool abort)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        var run = b.Runs.StartPlanAsync(b.Gated(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(50), wait)), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); b.Field.BeforeSnapshot = async token => { entered.TrySetResult(); await release.Task; };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var holds = b.Field.Holds; await Task.Delay(100);
            if (abort) b.Runs.Abort(); Assert.False(run.IsCompleted); Assert.Equal(holds, b.Field.Holds); Assert.True(b.Leases.IsLeased(b.Id, DateTimeOffset.UtcNow.AddYears(1)));
            b.Field.BeforeSnapshot = null; release.TrySetResult();
            if (abort) { await run; Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); }
            else await Assert.ThrowsAsync<PlanStepTimeoutException>(() => run);
            Assert.True(b.Field.Holds > holds); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Field.BeforeSnapshot = null; release.TrySetResult(); if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task ZeroImportedTimeoutHasNoDeadlineAndRemainsAbortable()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new PpmsExperimentPlanImporter().Parse(Node("wait_field", new() { ["timeout_s"] = 0, ["duration_s"] = 1 }), b.Bindings);
        Assert.IsType<WaitFieldStep>(plan.Root); var run = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); Assert.False(run.IsCompleted); b.Runs.Abort(); await run;
            Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows); Assert.True(b.Field.Holds > 0); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldReadFaultDoesNotRetryOrMaskErrorAndRefusedHoldRetainsOwnership(bool refuseHold)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var reads = 0;
        var wait = new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        var run = b.Runs.StartPlanAsync(b.Gated(new ResultDecoratorStep(new("mask"), ResultEffect.ForceSuccess, new RetryStep(new("retry"), 3, TimeSpan.Zero, wait))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); b.Field.RefuseHold = refuseHold;
            b.Field.BeforeSnapshot = token => { reads++; b.Field.BeforeSnapshot = null; throw new IOException("Field status read failed."); };
            var error = await Assert.ThrowsAnyAsync<Exception>(() => run); Assert.Contains("Field status read failed", error.ToString()); Assert.Equal(1, reads);
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(refuseHold, b.Runs.HasActiveRun);
            if (refuseHold) { Assert.True(b.Leases.IsLeased(b.Id, DateTimeOffset.UtcNow.AddYears(1))); b.Field.RefuseHold = false; await b.Runs.RecoverSafetyAsync(Token); }
            Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Field.RefuseHold = false; b.Field.BeforeSnapshot = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
}

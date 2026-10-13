using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830AcExecutionTests
{
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind, string? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var e = await events.Reader.ReadAsync(deadline.Token); if (e.Kind == kind && (step is null || e.Step?.Value == step)) return; }
    }
    [Fact]
    public async Task PauseIsolatesAndRestartsTheWholeAttemptWithoutRestoringClosedPaths()
    {
        var b = new Bench(); b.Meter.OnRead = _ => { if (b.Meter.Reads == 1) b.Runs.Pause(); return Task.CompletedTask; }; using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read()), Token); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); var closes = b.Matrix.Closes; var configurations = b.Meter.Configurations; b.AssertReserved();
        b.Runs.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(b.Runs.LatestDataSet.Rows); Assert.Equal(2, b.Meter.Reads); Assert.Equal(closes + 1, b.Matrix.Closes); Assert.Equal(configurations + 1, b.Meter.Configurations); b.AssertReleased();
    }
    [Fact]
    public async Task AbortDuringConnectedSettlingOpensPathsAndPublishesNoFrame()
    {
        var b = new Bench(); b.Matrix.AfterClose = () => { b.Runs.Abort(); return Task.CompletedTask; };
        await b.Runs.StartPlanAsync(b.Plan(b.Read(settle: TimeSpan.FromSeconds(30))), Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(0, b.Meter.Reads); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task AbortBeforeTheActiveStepStillIsolatesTheDeclaredBaselinePath()
    {
        var b = new Bench(baseline: true); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(new DelayStep(new("before-ac"), TimeSpan.FromSeconds(30)), b.Read()), Token); await Until(events, WorkbenchNotificationKind.StepStarted, "before-ac"); b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, b.Meter.Configurations); Assert.Equal(0, b.Meter.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Matrix.Opens > 0); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task PreparationFailureBeforeActiveStepStillRequiresBaselineIsolation()
    {
        var b = new Bench(baseline: true); b.Source.FailConfigure = true;
        var plan = new ExperimentPlan("Preparation fault", b.Read(), [new SourcePreparation(b.Source.Id, SourceFunction.Voltage, new(.1, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(0, b.Meter.Configurations); Assert.Null(b.Runs.LatestDataSet); Assert.True(b.Matrix.Opens > 0); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task LaterLogicalFailureDoesNotSubstituteAttemptIsolationForFreshRootProof()
    {
        Bench? b = null; var atCommit = 0; b = new(listener: new Listener(_ => { atCommit = b!.Matrix.Opens; return Task.CompletedTask; }));
        await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read(), new ResultDecoratorStep(new("later-failure"), ResultEffect.ForceFailure, new DelayStep(new("logical"), TimeSpan.Zero))), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Matrix.Opens > atCommit); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task UnknownRoutingAfterFrameCannotClaimIsolationAndRetainsEveryLeaseUntilRecovery()
    {
        var b = new Bench(); b.Meter.AfterRead = () => { b.Matrix.Unknown = true; return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); b.AssertReserved();
        b.Matrix.Unknown = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task ParentTimeoutWaitsForTheActualUncooperativeReadToFinishBeforeStopping()
    {
        var b = new Bench(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); b.Meter.IgnoreReadCancellation = true; b.Meter.OnRead = _ => gate.Task;
        var running = b.Runs.StartPlanAsync(b.Plan(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(200), b.Read())), Token);
        await b.Meter.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var aborts = b.Meter.Aborts; await Task.Delay(350); Assert.False(running.IsCompleted); Assert.Equal(aborts, b.Meter.Aborts); b.AssertReserved();
        gate.SetResult(); await Assert.ThrowsAsync<PlanStepTimeoutException>(() => running.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(b.Meter.ReadFinished.Task.IsCompleted); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
    [Fact]
    public async Task UncooperativeIsolationStopCannotBeOverlappedByRecovery()
    {
        Bench? b = null; b = new Bench(safetyTimeout: TimeSpan.FromSeconds(1), listener: new Listener(_ => { b!.Matrix.OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously); return Task.CompletedTask; }));
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read()), Token);
        await b.Matrix.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5))); var attempts = b.Matrix.Opens; b.AssertReserved();
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Meter.Aborts > 0);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.RecoverSafetyAsync(Token)); Assert.Equal(attempts, b.Matrix.Opens); b.AssertReserved();
        b.Matrix.OpenGate!.SetResult(); await b.Matrix.OpenFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Port completion precedes the session's readback/publication and stored task result.
        using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (b.Runs.HasActiveRun)
        {
            var error = await Record.ExceptionAsync(() => b.Runs.RecoverSafetyAsync(recoveryDeadline.Token, reconnect: true));
            if (error is null) break;
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("still in flight", error.Message);
            Assert.Equal(attempts, b.Matrix.Opens);
            await Task.Delay(TimeSpan.FromMilliseconds(10), recoveryDeadline.Token);
        }
        Assert.Equal(new[] { Unrelated }, (await b.Matrix.GetSnapshotAsync(Token)).Closed); b.AssertReleased();
    }
}

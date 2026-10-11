using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task IndependentObserversSeeTypedLocalStatePauseAndAbortWithoutOwningHardware()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var scope = new SubtreeStep(new("scope"), [TypedWrite("local_label", "label", new TextValue("child🧪")),
            TypedWrite("local_flag", "local.ready", new BooleanValue(true)), b.ReadSource("actual"), new DelayStep(new("wait"), TimeSpan.FromSeconds(30))]);
        var task = b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("body"), [TypedWrite("parent_label", "label", new TextValue("parent")), scope])), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            var first = await b.Runs.ObserveBlackboardAsync(id); Assert.Equal(RunExecutionPhase.Running, first.Phase); Assert.False(first.IsTerminal);
            Assert.Equal(id, first.Id); Assert.Equal(id, first.Snapshot!.Id); Assert.Equal(new[] { new StepId("scope") }, first.Scopes);
            Assert.Equal("child🧪", Assert.IsType<BlackboardText>(first.Snapshot.Entries[new("label")].Value).Value);
            Assert.True(Assert.IsType<BlackboardBoolean>(first.Snapshot.Entries[new("local.ready")].Value).Value);
            Assert.Equal(PhysicalUnit.Ampere, Assert.IsType<BlackboardQuantity>(first.Snapshot.Entries[new("actual.value")].Value).Value.Unit);
            Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            using var observerCancellation = new CancellationTokenSource();
            var cancelled = b.Runs.ObserveBlackboardAsync(id, first.Revision, observerCancellation.Token);
            var continuing = b.Runs.ObserveBlackboardAsync(id, first.Revision);
            Assert.False(cancelled.IsCompleted); Assert.False(continuing.IsCompleted);
            observerCancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(continuing.IsCompleted); Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            var paused = await continuing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(RunExecutionPhase.Paused, paused.Phase);
            Assert.True(paused.Revision > first.Revision); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            var resumed = b.Runs.ObserveBlackboardAsync(id, paused.Revision); b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed);
            Assert.Equal(RunExecutionPhase.Running, (await resumed.WaitAsync(TimeSpan.FromSeconds(5))).Phase);
            b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
            var terminal = await b.Runs.ObserveBlackboardAsync(id); Assert.True(terminal.IsTerminal); Assert.Empty(terminal.Scopes);
            Assert.Equal(MeasurementRunState.Aborted, terminal.Outcome); Assert.Equal(RunExecutionPhase.Idle, terminal.Phase);
            Assert.Same(b.Runs.LatestBlackboardSnapshot, terminal.Snapshot); Assert.Same(terminal.Snapshot, b.Runs.LatestDataSet!.Capture().Blackboard);
            Assert.Equal("parent", Assert.IsType<BlackboardText>(terminal.Snapshot!.Entries[new("label")].Value).Value);
            Assert.DoesNotContain(new ParameterId("local.ready"), terminal.Snapshot.Entries.Keys);
            Assert.Equal("child🧪", Assert.IsType<BlackboardText>(first.Snapshot.Entries[new("label")].Value).Value);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<ParameterId, BlackboardEntry>)first.Snapshot.Entries).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<StepId>)first.Scopes).Clear());
            Assert.False(b.Runs.HasActiveRun); Assert.Null(b.Runs.ActiveRunId); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        }
        finally { if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("abort")]
    [InlineData("timeout")]
    public async Task NestedLiveScopesRestoreRootAfterEveryExitAndOnlyExportSuccessfulValues(string exit)
    {
        var barrier = new BlackboardBarrier(blockRows: true); var b = new Bench(barrier);
        var inner = new SubtreeStep(new("inner"), [TypedWrite("inner_label", "label", new TextValue("inner")), b.ReadField("inside")], [new(new("ready"), new("inner.ready"))]);
        var outer = new SubtreeStep(new("outer"), [TypedWrite("local", "local.result", new TextValue("selected")), inner,
            TypedCheck("predicate", "ready", new BooleanValue(exit != "failure"))], outputs: [new(new("local.result"), new("exported"))]);
        ExperimentStep body = exit == "timeout" ? new TimeoutStep(new("deadline"), TimeSpan.FromSeconds(2), outer) : outer;
        var task = b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("body"), [TypedWrite("seed", "label", new TextValue("root")), TypedWrite("flag", "ready", new BooleanValue(true)), body])), Token);
        try
        {
            await barrier.Row.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            var live = await b.Runs.ObserveBlackboardAsync(id); Assert.Equal(new[] { new StepId("outer"), new StepId("inner") }, live.Scopes);
            Assert.Equal("inner", Assert.IsType<BlackboardText>(live.Snapshot!.Entries[new("label")].Value).Value);
            Assert.Equal(BlackboardWriteKind.SubtreeInput, live.Snapshot.Entries[new("inner.ready")].Kind);
            Assert.Equal(new StepId("inner"), live.Snapshot.Entries[new("inner.ready")].Source);
            var next = b.Runs.ObserveBlackboardAsync(id, live.Revision);
            if (exit == "abort") b.Runs.Abort(); else if (exit != "timeout") barrier.ContinueRow.TrySetResult();
            var error = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            if (exit is "success" or "abort") Assert.Null(error); else if (exit == "failure") Assert.IsType<PlanFailedException>(error); else Assert.IsType<PlanStepTimeoutException>(error);
            await next.WaitAsync(TimeSpan.FromSeconds(5)); var terminal = await b.Runs.ObserveBlackboardAsync(id);
            Assert.True(terminal.IsTerminal); Assert.Empty(terminal.Scopes); Assert.Same(b.Runs.LatestBlackboardSnapshot, terminal.Snapshot);
            Assert.Equal("root", Assert.IsType<BlackboardText>(terminal.Snapshot!.Entries[new("label")].Value).Value);
            Assert.Equal(new StepId("seed"), terminal.Snapshot.Entries[new("label")].Source);
            Assert.False(terminal.Snapshot.Entries.ContainsKey(new("inner.ready"))); Assert.False(terminal.Snapshot.Entries.ContainsKey(new("local.result")));
            Assert.Equal(exit == "success", terminal.Snapshot.Entries.ContainsKey(new("exported")));
            Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            Assert.Equal("inner", Assert.IsType<BlackboardText>(live.Snapshot.Entries[new("label")].Value).Value);
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5))); } }
    }

    [Fact]
    public async Task ConcurrentReadersCoalesceManyWritesAndNeverEnumerateMutableProducerState()
    {
        var barrier = new BlackboardBarrier(blockStarted: true); var b = new Bench(barrier);
        var plan = new ExperimentPlan("Observed loop", new SequenceStep(new("root"), [Local("seed", "counter", Number(0)), TypedWrite("label", "label", new TextValue("Si")),
            TypedWrite("flag", "ready", new BooleanValue(true)), new RepeatStep(new("repeat"), 1000, Mutate("increment", "counter", VariableMutation.Add, 1))]));
        var task = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            var initial = await b.Runs.ObserveBlackboardAsync(id); Assert.Equal(RunExecutionPhase.Starting, initial.Phase); Assert.Empty(initial.Snapshot!.Entries);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var readers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                var current = initial;
                do
                {
                    current = await b.Runs.ObserveBlackboardAsync(id, current.Revision, deadline.Token);
                    Assert.Equal(id, current.Id); Assert.Equal(id, current.Snapshot!.Id);
                    Assert.All(current.Snapshot.Entries, item => Assert.True(item.Value.Elapsed <= current.Snapshot.Elapsed));
                    if (current.Snapshot.Entries.TryGetValue(new("counter"), out var count)) Assert.InRange(Assert.IsType<BlackboardQuantity>(count.Value).Value.Value, 0, 1000);
                } while (!current.IsTerminal);
                return current;
            })).ToArray();
            barrier.ContinueStarted.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(10));
            var results = await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, result => { Assert.True(result.Revision > 1000); Assert.Equal(MeasurementRunState.Completed, result.Outcome); Assert.Same(b.Runs.LatestBlackboardSnapshot, result.Snapshot); });
            var slow = await b.Runs.ObserveBlackboardAsync(id, initial.Revision); Assert.True(slow.IsTerminal);
            Assert.Equal(1000, Assert.IsType<BlackboardQuantity>(slow.Snapshot!.Entries[new("counter")].Value).Value.Value);
            Assert.Empty(initial.Snapshot.Entries); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizerWaitIsObservableAndDeliveryFailureKeepsAcquisitionSnapshot(bool refuses)
    {
        var barrier = new BlackboardBarrier(blockFinalized: true) { RefuseFinalization = refuses }; var b = new Bench(barrier);
        var task = b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("body"), [TypedWrite("label", "label", new TextValue("Si")), b.ReadSource("read")])), Token);
        try
        {
            await barrier.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            var finalizing = await b.Runs.ObserveBlackboardAsync(id); Assert.False(finalizing.IsTerminal); Assert.Equal(RunExecutionPhase.Finalizing, finalizing.Phase);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            var waiting = b.Runs.ObserveBlackboardAsync(id, finalizing.Revision); Assert.False(waiting.IsCompleted);
            barrier.ContinueFinalized.TrySetResult(); var error = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(refuses, error is not null); await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            var terminal = await b.Runs.ObserveBlackboardAsync(id); Assert.True(terminal.IsTerminal);
            Assert.Equal(refuses ? MeasurementRunState.Faulted : MeasurementRunState.Completed, terminal.Outcome);
            Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet!.State); Assert.Same(b.Runs.LatestBlackboardSnapshot, terminal.Snapshot);
            Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5))); } }
    }

    [Fact]
    public async Task TerminalProducerStateDoesNotPretendFailedSafetyResourcesAreReleased()
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(b.ReadField("value")), Token)));
        var id = Assert.IsType<RunId>(b.Runs.ActiveRunId); var retained = await b.Runs.ObserveBlackboardAsync(id);
        Assert.True(retained.IsTerminal); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, retained.Phase); Assert.Equal(MeasurementRunState.Faulted, retained.Outcome);
        Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Same(retained.Snapshot, (await b.Runs.ObserveBlackboardAsync(id, retained.Revision).WaitAsync(TimeSpan.FromSeconds(5))).Snapshot);
        field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token);
        var recovered = await b.Runs.ObserveBlackboardAsync(id, retained.Revision); Assert.True(recovered.Revision > retained.Revision);
        Assert.Equal(RunExecutionPhase.Idle, recovered.Phase); Assert.Same(retained.Snapshot, recovered.Snapshot); Assert.Equal(retained.Outcome, recovered.Outcome);
        Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BranchAndAttemptRollbackRestoreProvenanceBeforeLiveNextRead(bool retry)
    {
        var barrier = new BlackboardBarrier(blockRows: true); var b = new Bench(barrier);
        var failed = new SequenceStep(new("failed"), [TypedWrite("discard", "label", new TextValue("discarded")), Check("false", false)]);
        ExperimentStep branch = retry ? new RetryStep(new("control"), 2, TimeSpan.Zero, new IfElseStep(new("choose"), new(new("control.attempt"), ComparisonOperator.Equal, new ConstantValue(Number(1))), failed, b.ReadField("second"))) :
            new FallbackStep(new("control"), [failed, b.ReadField("second")]);
        var task = b.Runs.StartPlanAsync(new("Rollback observation", new SequenceStep(new("root"), [TypedWrite("seed_label", "label", new TextValue("original")), Seed(branch)])), Token);
        try
        {
            await barrier.Row.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            var state = await b.Runs.ObserveBlackboardAsync(id); Assert.Empty(state.Scopes);
            Assert.Equal("original", Assert.IsType<BlackboardText>(state.Snapshot!.Entries[new("label")].Value).Value);
            Assert.Equal(new StepId("seed_label"), state.Snapshot.Entries[new("label")].Source);
            Assert.Equal(2, Assert.IsType<BlackboardQuantity>(state.Snapshot.Entries[new(retry ? "control.attempt" : "control.branch")].Value).Value.Value);
            barrier.ContinueRow.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(b.Runs.HasActiveRun);
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5))); } }
    }

    [Fact]
    public async Task RunAndRevisionGuardsNeverSwitchAnObserverToAnUnrelatedRun()
    {
        var b = new Bench(); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.ObserveBlackboardAsync(RunId.New()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => b.Runs.ObserveBlackboardAsync(null!));
        await b.Runs.StartPlanAsync(new("First", TypedWrite("first", "label", new TextValue("Si"))), Token);
        var id = b.Runs.LatestOutcome!.Id; var first = await b.Runs.ObserveBlackboardAsync(id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.ObserveBlackboardAsync(id, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.ObserveBlackboardAsync(id, long.MaxValue));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Runs.ObserveBlackboardAsync(id, cancellationToken: cancelled.Token));
        var same = await b.Runs.ObserveBlackboardAsync(id, first.Revision).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Same(first.Snapshot, same.Snapshot);
        await b.Runs.StartPlanAsync(new("Second", TypedWrite("second", "label", new TextValue("Ge"))), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.ObserveBlackboardAsync(id, first.Revision));
        var next = await b.Runs.ObserveBlackboardAsync(b.Runs.LatestOutcome!.Id); Assert.NotEqual(id, next.Id);
        Assert.Equal("Ge", Assert.IsType<BlackboardText>(next.Snapshot!.Entries[new("label")].Value).Value);
        Assert.Equal("Si", Assert.IsType<BlackboardText>(first.Snapshot!.Entries[new("label")].Value).Value);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("precancelled")]
    [InlineData("empty")]
    public async Task EmptyOrCancelledAdmissionClosesDiagnosticWaitersWithoutInventingValues(string mode)
    {
        var barrier = new BlackboardBarrier(blockStarted: mode == "starting"); var b = new Bench(barrier);
        using var cancellation = new CancellationTokenSource(); if (mode == "precancelled") cancellation.Cancel();
        var task = b.Runs.StartPlanAsync(new("Empty", new DelayStep(new("nothing"), TimeSpan.Zero)), cancellation.Token);
        try
        {
            Task<RunBlackboardObservation>? waiting = null;
            if (mode == "starting")
            {
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
                var starting = await b.Runs.ObserveBlackboardAsync(id); Assert.Equal(RunExecutionPhase.Starting, starting.Phase); Assert.Empty(starting.Snapshot!.Entries);
                waiting = b.Runs.ObserveBlackboardAsync(id, starting.Revision); Assert.False(waiting.IsCompleted); cancellation.Cancel();
            }
            await task.WaitAsync(TimeSpan.FromSeconds(5)); if (waiting is not null) await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            var terminal = await b.Runs.ObserveBlackboardAsync(b.Runs.LatestOutcome!.Id);
            Assert.True(terminal.IsTerminal); Assert.Equal(mode == "empty" ? MeasurementRunState.Completed : MeasurementRunState.Aborted, terminal.Outcome);
            Assert.NotNull(terminal.Snapshot); Assert.Empty(terminal.Snapshot.Entries); Assert.Empty(terminal.Scopes);
            Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task SlowStreamKeepsItsRunGenerationAfterAnotherRunReplacesTheLatestObservation()
    {
        var barrier = new BlackboardBarrier(blockStarted: true); var b = new Bench(barrier);
        var task = b.Runs.StartPlanAsync(new("First", TypedWrite("first", "label", new TextValue("Si"))), Token);
        try
        {
            await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            await using var stream = b.Runs.WatchBlackboardAsync(id).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync()); Assert.Equal(id, stream.Current.Id); Assert.False(stream.Current.IsTerminal);
            barrier.ContinueStarted.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(5)); var saved = b.Runs.LatestBlackboardSnapshot;
            await b.Runs.StartPlanAsync(new("Second", TypedWrite("second", "label", new TextValue("Ge"))), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.ObserveBlackboardAsync(id));
            Assert.True(await stream.MoveNextAsync()); Assert.Equal(id, stream.Current.Id); Assert.True(stream.Current.IsTerminal); Assert.Same(saved, stream.Current.Snapshot);
            Assert.Equal("Si", Assert.IsType<BlackboardText>(stream.Current.Snapshot!.Entries[new("label")].Value).Value);
            Assert.False(await stream.MoveNextAsync()); Assert.NotEqual(id, b.Runs.LatestOutcome!.Id);
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task CancellingOneStreamLeavesAnotherConsumerAndTheProducerAlive()
    {
        var barrier = new BlackboardBarrier(blockStarted: true); var b = new Bench(barrier);
        var task = b.Runs.StartPlanAsync(new("Observed", TypedWrite("seed", "label", new TextValue("Si"))), Token);
        try
        {
            await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var id = Assert.IsType<RunId>(b.Runs.ActiveRunId);
            using var cancellation = new CancellationTokenSource();
            await using var first = b.Runs.WatchBlackboardAsync(id, cancellation.Token).GetAsyncEnumerator();
            await using var second = b.Runs.WatchBlackboardAsync(id).GetAsyncEnumerator();
            Assert.True(await first.MoveNextAsync()); Assert.True(await second.MoveNextAsync());
            var cancelled = first.MoveNextAsync().AsTask(); var continuing = second.MoveNextAsync().AsTask();
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(continuing.IsCompleted); Assert.True(b.Runs.HasActiveRun);
            barrier.ContinueStarted.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(await continuing.WaitAsync(TimeSpan.FromSeconds(5)));
            while (!second.Current.IsTerminal) Assert.True(await second.MoveNextAsync());
            Assert.Equal(MeasurementRunState.Completed, second.Current.Outcome); Assert.Same(b.Runs.LatestBlackboardSnapshot, second.Current.Snapshot);
            Assert.False(await second.MoveNextAsync()); Assert.False(b.Runs.HasActiveRun);
        }
        finally { barrier.ReleaseAll(); if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    private sealed class BlackboardBarrier(bool blockStarted = false, bool blockRows = false, bool blockFinalized = false) : IRunResultListener
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Row { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finalizing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueFinalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RefuseFinalization { get; init; }
        public async Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) { Started.TrySetResult(); if (blockStarted) await ContinueStarted.Task.WaitAsync(token); }
        public async Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { Row.TrySetResult(); if (blockRows) await ContinueRow.Task.WaitAsync(token); }
        public async Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token)
        { Finalizing.TrySetResult(); if (blockFinalized) await ContinueFinalized.Task.WaitAsync(token); if (RefuseFinalization) throw new IOException("Final delivery refused."); }
        public void ReleaseAll() { ContinueStarted.TrySetResult(); ContinueRow.TrySetResult(); ContinueFinalized.TrySetResult(); }
    }
}

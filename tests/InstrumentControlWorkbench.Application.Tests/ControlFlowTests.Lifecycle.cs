using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Theory]
    [InlineData("force_success")]
    [InlineData("force_failure")]
    [InlineData("inverter")]
    [InlineData("fallback")]
    [InlineData("retry")]
    public async Task CancellationPropagatesThroughEveryControlNodeBeforeAnotherBranchOrAttempt(string kind)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); using var cancellation = new CancellationTokenSource();
        var task = b.Runs.StartPlanAsync(b.Powered(Wrap(kind, new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), b.ReadField("alternative"))), cancellation.Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("wait")); Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            cancellation.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            var remaining = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var fact)) remaining.Add(fact);
            Assert.DoesNotContain(remaining, f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == new StepId("wrapper"));
        }
        finally { if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task PauseDuringBackoffKeepsGroupAndRestoresOutputWithoutConsumingRemainingDelay()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var body = new SequenceStep(new("attempt"), [b.ReadSource("electrical"), b.ReadField("field"),
            new ConditionStep(new("ready"), new(new("retry.attempt"), ComparisonOperator.Equal, new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]);
        var retry = new RetryStep(new("retry"), 3, TimeSpan.FromMilliseconds(500), body);
        var task = b.Runs.StartPlanAsync(b.Powered(retry), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepCompleted, new("attempt")); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.True(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(0, PhysicalUnit.Volt), Token));
            await Task.Delay(650); Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count);
            b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            await Task.Delay(60); Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count);
            await task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(4, b.Runs.LatestDataSet.Rows.Count);
            Assert.Equal(new[] { 1.0, 1, 2, 2 }, b.Runs.LatestDataSet.Rows.Select(r => r.Values[retry.AttemptColumn].Value));
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task AbortDuringRetryBackoffRetainsFirstAttemptRowsAndPreventsLaterAcquisition()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var retry = new RetryStep(new("retry"), 3, TimeSpan.FromSeconds(30), new SequenceStep(new("attempt"), [b.ReadSource("value"), Check("fail", false)]));
        var task = b.Runs.StartPlanAsync(b.Powered(Seed(retry)), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepCompleted, new("attempt")); b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Runs.LatestDataSet.Rows[0].Values[retry.AttemptColumn].Value);
            Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        }
        finally { if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSafetyCleanupOverridesForcedSuccessAndPreservesLogicalCauseAndWholeGroup(bool forceSuccess)
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        var plan = b.Powered(new ResultDecoratorStep(new("effect"), forceSuccess ? ResultEffect.ForceSuccess : ResultEffect.ForceFailure, b.ReadField("value")));
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State);
        Assert.Contains(b.Runs.LatestOutcome!.Errors, e => e.Message.Contains("hold refused", StringComparison.Ordinal));
        Assert.Equal(!forceSuccess, b.Runs.LatestOutcome.Errors.Any(e => e is PlanFailedException));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.True(b.Leases.IsLeased(field.Definition.Device.Id, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token));
        field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token);
        Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.True(field.HoldCalls >= 2);
    }

    [Fact]
    public async Task ListenerFaultEscapesNestedForceSuccessAndFallbackWithCommittedRowPreserved()
    {
        var listener = new RefusingListener(); var b = new Bench(listener);
        var body = new FallbackStep(new("choose"), [new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, b.ReadSource("value")), b.ReadField("alternative")]);
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(body), Token)); Assert.NotNull(error); Assert.Contains("listener refused", error.Message);
        Assert.Equal(1, listener.Rows); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Runs.LatestDataSet.Rows[0].Values[new("choose.branch")].Value);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    private sealed class RefusingListener : IRunResultListener
    {
        public int Rows { get; private set; }
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { Rows++; throw new IOException("listener refused"); }
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class RefusingHoldAxis(MockAxisController inner) : IAxisControllerPort
    {
        public bool Refuse { get; set; } = true;
        public int HoldCalls { get; private set; }
        public AxisControllerDefinition Definition => inner.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity target, CancellationToken token) => inner.SetTargetAsync(target, token);
        public Task SetRateAsync(Quantity rate, CancellationToken token) => inner.SetRateAsync(rate, token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task HoldAsync(CancellationToken token) { HoldCalls++; if (Refuse) throw new IOException("controller hold refused"); return inner.HoldAsync(token); }
    }
}

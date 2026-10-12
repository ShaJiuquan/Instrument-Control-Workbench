using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControllerWaitExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData("wait_angle", false)]
    [InlineData("wait_angle_state", false)]
    [InlineData("wait", true)]
    public async Task AngleWaitUsesFreshTargetWithoutProgrammingIt(string name, bool generic)
    {
        var b = new Bench(); var p = new JsonObject { ["timeout_s"] = 1, ["poll_ms"] = 1 }; if (generic) p["wait_kind"] = "wait_angle";
        var plan = new PpmsExperimentPlanImporter().Parse(Node(name, p), b.Bindings);
        var wrapper = Assert.IsType<TimeoutStep>(plan.Root); Assert.Equal(TimeSpan.FromSeconds(1), wrapper.Timeout);
        var wait = Assert.IsType<WaitAngleStep>(wrapper.Body); Assert.Null(wait.Target); Assert.Equal(TimeSpan.FromMilliseconds(100), wait.PollInterval);
        var codec = new JsonExperimentPlanDocument(); await b.Runs.StartPlanAsync(codec.Parse(codec.Serialize(plan)), Token);
        Assert.Equal(0, b.Angle.TargetWrites); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet.State); Assert.False(b.Runs.HasActiveRun);
        Assert.True(b.Angle.Holds > 0);
    }
    [Fact]
    public async Task OmittedTargetTracksLaterCoherentObservationRatherThanFirstCachedTarget()
    {
        var b = new Bench(); var target = new Quantity(10, PhysicalUnit.Degree); var position = new Quantity(0, PhysicalUnit.Degree);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = b.Hub.Subscribe();
        var run = b.Runs.StartPlanAsync(new("Live target", new SequenceStep(new("root"), [new DelayStep(new("marker"), TimeSpan.FromMilliseconds(100)),
            new WaitAngleStep(new("angle.wait"), b.Id, new(0, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(20))])), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted);
            b.Angle.Observation = snapshot => { var result = snapshot with { Target = target, Readback = position }; observed.TrySetResult(); if (target == position) b.Angle.Observation = null; return result; };
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)); target = position;
            await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, b.Angle.TargetWrites); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Angle.Observation = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Theory]
    [InlineData("target_deg")]
    [InlineData("target")]
    [InlineData("angle")]
    public async Task ExplicitTargetIsOnlyComparisonAndDoesNotSetController(string key)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var waitPlan = new PpmsExperimentPlanImporter().Parse(Node("wait_angle", new() { [key] = 0, ["tolerance"] = 0, ["timeout"] = 1 }), b.Bindings);
        var run = b.Runs.StartPlanAsync(new("Comparison only", new SequenceStep(new("root"), [new DelayStep(new("marker"), TimeSpan.FromMilliseconds(100)), waitPlan.Root])), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted);
            b.Angle.Observation = snapshot => { b.Angle.Observation = null; return snapshot with { Target = new(10, PhysicalUnit.Degree), Readback = new(0, PhysicalUnit.Degree) }; };
            await run; Assert.Equal(0, b.Angle.TargetWrites); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Angle.Observation = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task ZeroTimeoutWaitsUntilAbortAndDrainsBlockedObservation()
    {
        var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = false; b.Angle.BeforeSnapshot = async token =>
        {
            if (!active) return;
            entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } finally { drained.TrySetResult(); }
        };
        using var events = b.Hub.Subscribe();
        var wait = new PpmsExperimentPlanImporter().Parse(Node("wait_angle", new() { ["timeout_s"] = 0 }), b.Bindings);
        Assert.IsType<WaitAngleStep>(wait.Root);
        // A marker delay gates the probe after preparation so only wait IO blocks.
        var plan = new ExperimentPlan("Cancelable live wait", new SequenceStep(new("root"), [new DelayStep(new("marker"), TimeSpan.FromMilliseconds(100)), wait.Root]));
        var run = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted); active = true; await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); active = false;
            b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(drained.Task.IsCompleted); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State);
            Assert.False(b.Runs.HasActiveRun); Assert.Empty(b.Runs.LatestDataSet.Rows); Assert.True(b.Angle.Holds > 0);
        }
        finally { active = false; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task FiniteWaitDeadlineIncludesPauseAndReportsTimeoutInsteadOfSuccess()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new PpmsExperimentPlanImporter().Parse(Node("wait_angle", new() { ["timeout_s"] = 0.25, ["poll_ms"] = 100 }), b.Bindings);
        var run = b.Runs.StartPlanAsync(new("Move and wait", new SequenceStep(new("root"), [new SetParameterStep(new("move"), new(b.Id, b.Angle.Definition.Target.Id), new ConstantValue(new(10, PhysicalUnit.Degree))), plan.Root])), Token);
        try
        {
            await UntilStep(events, "ppms.root.wait"); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            var error = await Assert.ThrowsAsync<PlanStepTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(new StepId("ppms.root"), error.Step);
            Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet!.State); Assert.False(b.Runs.HasActiveRun); Assert.True(b.Angle.Holds > 0);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Theory]
    [InlineData("units")]
    [InlineData("disconnected")]
    [InlineData("read_failure")]
    [InlineData("hold_failure")]
    public async Task InvalidAngleObservationAndFailedHoldKeepTruthfulFaultAndOwnership(string fault)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new ExperimentPlan("Failure during wait", new SequenceStep(new("root"), [new DelayStep(new("marker"), TimeSpan.FromMilliseconds(100)),
            new WaitAngleStep(new("wait"), b.Id, new(0, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(20))]));
        var run = b.Runs.StartPlanAsync(plan, Token);
        await Until(events, WorkbenchNotificationKind.StepStarted);
        if (fault == "units") b.Angle.Observation = s => { b.Angle.Observation = null; return s with { Readback = new(0, PhysicalUnit.Tesla) }; };
        else if (fault == "disconnected") b.Angle.Observation = s => s with { Connected = false, Moving = true };
        else b.Angle.BeforeSnapshot = token => { if (fault != "hold_failure") b.Angle.BeforeSnapshot = null; throw new IOException("Angle observation failed."); };
        b.Angle.RefuseHold = fault == "hold_failure";
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => run); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun == (fault is "disconnected" or "hold_failure"));
            b.Angle.Observation = null; b.Angle.BeforeSnapshot = null; b.Angle.RefuseHold = false;
            if (b.Runs.HasActiveRun) await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Angle.Observation = null; b.Angle.BeforeSnapshot = null; b.Angle.RefuseHold = false; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task StandaloneWaitDoesNotParkExistingMotionAtAdmissionOrInventReachedTarget()
    {
        var b = new Bench(); await b.Angle.ConnectAsync(Token); await b.Angle.SetRateAsync(new(1, PhysicalUnit.DegreePerSecond), Token); await b.Angle.SetTargetAsync(new(10, PhysicalUnit.Degree), Token);
        var snapshot = await b.Angle.GetSnapshotAsync(Token); Assert.True(snapshot.Moving); Assert.Equal(0, b.Angle.Holds);
        var plan = new ExperimentPlan("Wait for existing motion", new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(50), new WaitAngleStep(new("wait"), b.Id, new(0.1, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(20))));
        await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(1, b.Angle.TargetWrites); Assert.True(b.Angle.Holds > 0);
        Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Angle.GetSnapshotAsync(Token)).Moving);
    }
    private static string Node(string name, JsonObject? p = null) => new JsonObject { ["type"] = "command", ["name"] = name, ["params"] = p ?? new(), ["source_line"] = 7 }.ToJsonString();
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private static async Task UntilStep(NotificationSubscription events, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var item = await events.Reader.ReadAsync(deadline.Token); if (item.Kind == WorkbenchNotificationKind.StepStarted && item.Step?.Value == id) return; }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public AngleProbe Angle { get; } = new();
        public InstrumentId Id => Angle.Definition.Device.Id; public DeviceRegistry Devices { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentImportBindings Bindings => new([], [KeyValuePair.Create("angle", Angle.Definition)]);
        public Bench(IRunResultListener? listener = null) { Devices = new([new AxisControllerSession(Angle, Hub)], Leases); Runs = new(Devices, Hub, listener is null ? [] : [listener]); }
    }
    private sealed class AngleProbe : IAxisControllerPort
    {
        private readonly MockAxisController inner = MockEnvironmentCatalog.Angle();
        public AxisControllerDefinition Definition => inner.Definition; public Func<AxisControllerSnapshot, AxisControllerSnapshot>? Observation { get; set; }
        public Func<CancellationToken, Task>? BeforeSnapshot { get; set; }
        public int TargetWrites { get; private set; }
        public int Holds { get; private set; }
        public bool RefuseHold { get; set; }
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) { if (BeforeSnapshot is { } action) await action(token); var s = await inner.GetSnapshotAsync(token); return Observation?.Invoke(s) ?? s; }
        public Task SetTargetAsync(Quantity target, CancellationToken token) { TargetWrites++; return inner.SetTargetAsync(target, token); }
        public Task SetRateAsync(Quantity rate, CancellationToken token) => inner.SetRateAsync(rate, token);
        public async Task HoldAsync(CancellationToken token) { Holds++; if (RefuseHold) throw new IOException("Hold refused."); await inner.HoldAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
}

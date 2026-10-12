using System.Diagnostics;
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

public sealed partial class FieldWaitExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData("wait_field")]
    [InlineData("wait_field_holding")]
    [InlineData("wait_field_stable")]
    [InlineData("wait")]
    public async Task AliasesAndGenericWaitUseBoundFieldLiveTargetWithoutWritesOrRows(string name)
    {
        var b = new Bench(); var p = new JsonObject { ["timeout_s"] = 1, ["poll_ms"] = 1 }; if (name == "wait") p["wait_kind"] = "wait_field";
        var codec = new JsonExperimentPlanDocument(); var plan = new PpmsExperimentPlanImporter().Parse(Node(name, p), b.Bindings);
        var timeout = Assert.IsType<TimeoutStep>(plan.Root); var wait = Assert.IsType<WaitFieldStep>(timeout.Body);
        Assert.Equal(new(0.0005, PhysicalUnit.Tesla), wait.Tolerance); Assert.Equal(TimeSpan.FromMilliseconds(100), wait.PollInterval); Assert.Equal(TimeSpan.Zero, wait.StableFor);
        await b.Runs.StartPlanAsync(codec.Parse(codec.Serialize(plan)), Token); Assert.Equal(0, b.Field.TargetWrites); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false, 0.0005, true)]
    [InlineData(false, -0.0005, true)]
    [InlineData(false, 0.00050001, false)]
    [InlineData(true, 0, false)]
    public async Task ToleranceIsInclusiveButMatchingValueCannotHideOngoingRamp(bool moving, double actual, bool satisfied)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = b.Runs.StartPlanAsync(b.Gated(new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(10))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted);
            b.Field.Observe = s => { entered.TrySetResult(); return s with { Target = new(0, PhysicalUnit.Tesla), Readback = new(actual, PhysicalUnit.Tesla), Moving = moving }; };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (satisfied) await run.WaitAsync(TimeSpan.FromSeconds(5));
            else { Assert.False(run.IsCompleted); b.Field.Observe = null; b.Runs.Abort(); await run; Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); }
            Assert.False(b.Runs.HasActiveRun); Assert.Equal(0, b.Field.TargetWrites);
        }
        finally { b.Field.Observe = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task FieldWaitFollowsChangedLiveTargetWithoutPinningFirstPoll()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new Quantity(1, PhysicalUnit.Tesla);
        var run = b.Runs.StartPlanAsync(b.Gated(new WaitFieldStep(new("wait"), b.Id, new(0, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(10))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted);
            b.Field.Observe = s => { var next = s with { Target = target, Readback = new(0, PhysicalUnit.Tesla), Moving = false }; entered.TrySetResult(); if (target.Value == 0) b.Field.Observe = null; return next; };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); target = new(0, PhysicalUnit.Tesla); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, b.Field.TargetWrites); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Field.Observe = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingOrOutOfToleranceObservationRestartsDurationEvidence(bool moving)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var polls = 0; long resumedAt = 0;
        var required = TimeSpan.FromMilliseconds(60);
        var run = b.Runs.StartPlanAsync(b.Gated(new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), required, TimeSpan.FromMilliseconds(10))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted);
            b.Field.BeforeSnapshot = async token => { if (polls == 1) await Task.Delay(100, token); };
            b.Field.Observe = s =>
            {
                var count = ++polls; if (count == 3) { resumedAt = Stopwatch.GetTimestamp(); b.Field.BeforeSnapshot = null; }
                return s with { Target = new(0, PhysicalUnit.Tesla), Readback = new(count == 2 && !moving ? 0.01 : 0, PhysicalUnit.Tesla), Moving = count == 2 && moving };
            };
            await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(resumedAt != 0); Assert.True(Stopwatch.GetElapsedTime(resumedAt) >= required); Assert.True(polls >= 4); Assert.False(b.Runs.HasActiveRun);
        }
        finally { b.Field.Observe = null; b.Field.BeforeSnapshot = null; if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Fact]
    public async Task ExistingMotionIsNotParkedAtAdmissionAndFiniteDeadlineIsFault()
    {
        var b = new Bench(); await b.Field.ConnectAsync(Token); await b.Field.SetTargetAsync(new(1, PhysicalUnit.Tesla), Token);
        var plan = new ExperimentPlan("Wait on existing ramp", new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(50), new WaitFieldStep(new("wait"), b.Id, new(0.0005, PhysicalUnit.Tesla), TimeSpan.Zero, TimeSpan.FromMilliseconds(10))));
        await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(1, b.Field.TargetWrites); Assert.True(b.Field.Holds > 0); Assert.False(b.Runs.HasActiveRun);
    }
    private static string Node(string name, JsonObject? p = null) => new JsonObject { ["type"] = "command", ["name"] = name, ["params"] = p ?? new(), ["source_line"] = 9 }.ToJsonString();
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public FieldProbe Field { get; } = new();
        public InstrumentId Id => Field.Definition.Device.Id; public DeviceRegistry Devices { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentImportBindings Bindings => new([], [KeyValuePair.Create("field", Field.Definition)]);
        public Bench(IRunResultListener? listener = null) { Devices = new([new AxisControllerSession(Field, Hub)], Leases); Runs = new(Devices, Hub, listener is null ? [] : [listener]); }
        public ExperimentPlan Gated(ExperimentStep wait) => new("Gated field wait", new SequenceStep(new("root"), [new DelayStep(new("marker"), TimeSpan.FromMilliseconds(100)), wait]));
    }
    private sealed class FieldProbe : IAxisControllerPort
    {
        private readonly MockAxisController inner = MockEnvironmentCatalog.Field();
        public AxisControllerDefinition Definition => inner.Definition; public Func<AxisControllerSnapshot, AxisControllerSnapshot>? Observe { get; set; }
        public Func<CancellationToken, Task>? BeforeSnapshot { get; set; }
        public int TargetWrites { get; private set; }
        public int Holds { get; private set; }
        public bool RefuseHold { get; set; }
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) { if (BeforeSnapshot is { } action) await action(token); var s = await inner.GetSnapshotAsync(token); return Observe?.Invoke(s) ?? s; }
        public Task SetTargetAsync(Quantity target, CancellationToken token) { TargetWrites++; return inner.SetTargetAsync(target, token); }
        public Task SetRateAsync(Quantity rate, CancellationToken token) => inner.SetRateAsync(rate, token);
        public async Task HoldAsync(CancellationToken token) { Holds++; if (RefuseHold) throw new IOException("Field hold refused."); await inner.HoldAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
}

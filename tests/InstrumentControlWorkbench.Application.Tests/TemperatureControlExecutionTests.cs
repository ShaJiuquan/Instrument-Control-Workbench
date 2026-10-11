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

public sealed partial class TemperatureControlExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private static Quantity Rate(double value) => new(value, PhysicalUnit.KelvinPerMinute);

    [Theory]
    [InlineData(TemperatureControlAction.RampOn, false, false, true, false)]
    [InlineData(TemperatureControlAction.RampOff, true, false, false, false)]
    [InlineData(TemperatureControlAction.ControlOn, false, false, false, true)]
    [InlineData(TemperatureControlAction.ControlOff, true, true, true, false)]
    public async Task IndependentManualCommandPreservesTargetOtherFlagAndUnspecifiedRate(TemperatureControlAction action, bool ramp, bool control, bool expectedRamp, bool expectedControl)
    {
        var b = new Bench(); await b.Devices.ConnectAsync(b.Id, Token);
        await b.Port.Mock.ConfigureControlAsync(new(ramp, control), Rate(3), Token);
        await b.Port.Mock.SetTargetAsync(K(305), Token);
        await b.Devices.ConfigureTemperatureAsync(b.Id, action, action == TemperatureControlAction.RampOn ? Rate(7) : null, Token);
        Assert.Equal(new TemperatureControlConfiguration(expectedRamp, expectedControl), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        var snapshot = await b.Port.Mock.GetSnapshotAsync(Token);
        Assert.Equal(K(305), snapshot.Target); Assert.Equal(Rate(action == TemperatureControlAction.RampOn ? 7 : 3), snapshot.Rate);
    }

    [Fact]
    public async Task DisabledControlNeverFabricatesMovementAndExplicitOnStartsAtStoredTarget()
    {
        var b = new Bench(); await b.Devices.ConnectAsync(b.Id, Token);
        await b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.ControlOff, null, Token);
        await b.Devices.WriteAsync(b.Id, b.Port.Definition.Target.Id, K(305), Token);
        b.Clock.Advance(TimeSpan.FromMinutes(2)); var disabled = await b.Port.Mock.GetSnapshotAsync(Token);
        Assert.False(disabled.Moving); Assert.Equal(K(300), disabled.Readback); Assert.Equal(K(305), disabled.Target);
        await b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.RampOn, Rate(5), Token);
        b.Clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(K(300), (await b.Port.Mock.GetSnapshotAsync(Token)).Readback);
        await b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.ControlOn, null, Token);
        b.Clock.Advance(TimeSpan.FromSeconds(30)); var enabled = await b.Port.Mock.GetSnapshotAsync(Token);
        Assert.Equal(K(302.5), enabled.Readback); Assert.True(enabled.Moving);
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("target")]
    [InlineData("rate")]
    [InlineData("readback")]
    [InlineData("readback-late")]
    [InlineData("throw")]
    public async Task UnconfirmedCommandFaultsStopsAndReleasesOnlyAfterFreshHold(string fault)
    {
        var b = new Bench(); b.Port.Fault = fault;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(TemperatureControlAction.ControlOff), Token));
        if (fault == "throw") Assert.Same(b.Port.Failure, error);
        else Assert.Contains("did not confirm", error.Message);
        Assert.Equal(1, b.Port.Holds); Assert.False(b.Runs.HasActiveRun);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        var snapshot = await b.Port.Mock.GetSnapshotAsync(Token); Assert.Equal(snapshot.Readback, snapshot.Target);
        Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task UnsupportedConfigurationCapabilityFailsBeforeConnectingAnyResource()
    {
        var b = new Bench(); var field = MockEnvironmentCatalog.Field();
        var hub = new NotificationHub(); var source = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([source, new AxisControllerSession(field, hub)], leases);
        var runs = new MeasurementRunService(source, leases, hub, deviceRegistry: registry);
        Assert.Throws<NotSupportedException>(() => runs.PreviewPlan(new("Invalid", new TemperatureControlStep(new("command"), field.Definition.Device.Id, TemperatureControlAction.ControlOff))));
        Assert.Equal(0, b.Port.Applies);
        Assert.False((await field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData(TemperatureControlAction.RampOn)]
    [InlineData(TemperatureControlAction.RampOff)]
    [InlineData(TemperatureControlAction.ControlOn)]
    [InlineData(TemperatureControlAction.ControlOff)]
    public async Task CancellationInsideCommandUsesIndependentCleanup(TemperatureControlAction action)
    {
        var b = new Bench(); b.Port.Block = true; using var cancelled = new CancellationTokenSource();
        var run = b.Runs.StartPlanAsync(b.Plan(action), cancelled.Token);
        await b.Port.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, b.Port.Holds); Assert.False(b.Runs.HasActiveRun);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrControlEnablingHoldRetainsWholeGroupUntilExplicitRecovery(bool enablesControl)
    {
        var b = new Bench(); b.Port.FailHold = !enablesControl; b.Port.EnableOnHold = enablesControl;
        var plan = new ExperimentPlan("Group", new SequenceStep(new("root"), [b.Command(TemperatureControlAction.ControlOff),
            new ReadStep(new("read"), [new(new("source.current"), new(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]) ]));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.ControlOn, null, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConnectAsync(b.Source.Id, Token));
        b.Port.FailHold = false; b.Port.EnableOnHold = false;
        await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("ramp")]
    [InlineData("control")]
    [InlineData("rate")]
    public async Task PauseRetainsDisabledControlAndRejectsExternalFlagOrRateDrift(string drift)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new ExperimentPlan("Paused temperature", new SequenceStep(new("root"), [b.Command(TemperatureControlAction.ControlOff), new DelayStep(new("wait"), TimeSpan.FromMilliseconds(400)), b.Read()]));
        var run = b.Runs.StartPlanAsync(plan, Token); await b.Port.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.RampOn, Rate(3), Token));
        if (drift != "none") await b.Port.Mock.ConfigureControlAsync(new(drift != "ramp", drift == "control"), drift == "rate" ? Rate(6) : null, Token);
        b.Runs.Resume();
        if (drift == "none") { await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled); }
        else { var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Contains("changed while paused", error.Message); Assert.Empty(b.Runs.LatestDataSet!.Rows); }
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task AbortWhilePausedLeavesControlDisabledAndReleasesAfterHold()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var plan = new ExperimentPlan("Abort disabled control", new SequenceStep(new("root"), [b.Command(TemperatureControlAction.ControlOff),
            new DelayStep(new("wait"), TimeSpan.FromMinutes(1)), b.Read()]));
        var run = b.Runs.StartPlanAsync(plan, Token); await b.Port.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); b.Runs.Abort();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(b.Runs.HasActiveRun); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Port.Mock.ReadControlConfigurationAsync(Token)).ControlEnabled);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableFlagsCannotSuppressEmergencyHoldAndCannotAuthorizeRelease(bool holdFails)
    {
        var b = new Bench(); await b.Port.ConnectAsync(Token);
        await b.Port.Mock.SetTargetAsync(K(305), Token);
        b.Port.FailNextConfigurationRead = true; b.Port.FailHold = holdFails;
        var axis = new AxisControllerSession(b.Port, b.Hub);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => axis.StopDeviceAsync(Token));
        Assert.Equal(1, b.Port.Holds);
        if (holdFails)
        {
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.Same(b.Port.HoldReadFailure, aggregate.InnerExceptions[0]);
            Assert.Contains("hold refused", aggregate.InnerExceptions[1].Message);
        }
        else { Assert.Same(b.Port.HoldReadFailure, error); Assert.Equal(K(300), (await b.Port.Mock.GetSnapshotAsync(Token)).Target); }
    }

    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }

    private sealed class Bench
    {
        public ManualClock Clock { get; } = new();
        public TemperatureProbe Port { get; }
        public NotificationHub Hub { get; } = new();
        public MockKeithley2450SourceMeter Source { get; } = new();
        public DeviceRegistry Devices { get; }
        public MeasurementRunService Runs { get; }
        public InstrumentId Id => Port.Definition.Device.Id;
        public ExperimentImportBindings Bindings => new([], [KeyValuePair.Create("temperature", Port.Definition)]);
        public Bench(IRunResultListener? listener = null)
        {
            Port = new(Clock); var manager = new InstrumentManager(Source, Hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            Devices = new([manager, new AxisControllerSession(Port, Hub)], leases);
            Runs = new(manager, leases, Hub, listener is null ? [] : [listener], deviceRegistry: Devices);
        }
        public TemperatureControlStep Command(TemperatureControlAction action) => new(new("command"), Id, action, action == TemperatureControlAction.RampOn ? Rate(7) : null);
        public ExperimentPlan Plan(TemperatureControlAction action) => new("Temperature command", Command(action));
        public ReadStep Read() => new(new("read"), [new(new("temperature.actual"), new(Id, Port.Definition.Readback.Id))]);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private sealed class TemperatureProbe(ManualClock clock) : ITemperatureConfigurationPort
    {
        public MockAxisController Mock { get; } = MockEnvironmentCatalog.Temperature(clock: clock);
        public AxisControllerDefinition Definition => Mock.Definition;
        public string? Fault { get; set; }
        public IOException Failure { get; } = new("temperature configuration failed");
        public IOException HoldReadFailure { get; } = new("control flags unreadable");
        public bool FailNextConfigurationRead { get; set; }
        public bool InconsistentNextConfigurationRead { get; set; }
        public bool BlockNextConfigurationRead { get; set; }
        public TaskCompletionSource ConfigurationBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; }
        public bool FailHold { get; set; }
        public bool EnableOnHold { get; set; }
        public int Applies { get; private set; }
        public int Holds { get; private set; }
        private bool configured;
        private bool readFaulted;
        private int configurationReads;
        public TaskCompletionSource Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => Mock.ConnectAsync(token);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            var snapshot = await Mock.GetSnapshotAsync(token);
            return configured ? Fault switch { "target" => snapshot with { Target = K(301) }, "rate" => snapshot with { Rate = Rate(9) }, _ => snapshot } : snapshot;
        }
        public Task SetTargetAsync(Quantity target, CancellationToken token) => Mock.SetTargetAsync(target, token);
        public Task SetRateAsync(Quantity rate, CancellationToken token) => Mock.SetRateAsync(rate, token);
        public Task PrepareSoftwareRampAsync(Quantity target, CancellationToken token) => Mock.PrepareSoftwareRampAsync(target, token);
        public List<Quantity> HardwareRampTargets { get; } = [];
        public async Task SetHardwareRampAsync(Quantity rate, CancellationToken token)
        {
            HardwareRampTargets.Add((await Mock.GetSnapshotAsync(token)).Target);
            await Mock.SetHardwareRampAsync(rate, token);
        }
        public async Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token)
        {
            if (BlockNextConfigurationRead) { BlockNextConfigurationRead = false; ConfigurationBlocked.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (FailNextConfigurationRead) { FailNextConfigurationRead = false; throw HoldReadFailure; }
            var result = await Mock.ReadControlConfigurationAsync(token);
            if (InconsistentNextConfigurationRead) { InconsistentNextConfigurationRead = false; return result with { ControlEnabled = !result.ControlEnabled }; }
            if (configured && !readFaulted && (Fault == "readback" || Fault == "readback-late" && ++configurationReads == 2))
            { readFaulted = true; return result with { ControlEnabled = !result.ControlEnabled }; }
            return result;
        }
        public async Task ConfigureControlAsync(TemperatureControlConfiguration configuration, Quantity? rate, CancellationToken token)
        {
            Applies++; configured = true; Applied.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, token);
            if (Fault == "throw") throw Failure;
            if (Fault != "ignored") await Mock.ConfigureControlAsync(configuration, rate, token);
        }
        public async Task HoldAsync(CancellationToken token)
        {
            configured = false; Holds++;
            if (FailHold) throw new IOException("temperature hold refused");
            await Mock.HoldAsync(token);
            if (EnableOnHold) await Mock.ConfigureControlAsync(new(true, true), null, token);
        }
        public Task DisconnectAsync(CancellationToken token) => Mock.DisconnectAsync(token);
    }
}

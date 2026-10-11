using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TemperatureControlExecutionTests
{
    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("ignored")]
    public async Task FailedDisconnectCannotKeepPriorStableEvidence(string failure)
    {
        var b = new Bench(useStateClock: true);
        await b.Devices.ConnectAsync(b.Id, Token);
        b.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        b.Port.DisconnectFault = failure;
        using var events = b.Hub.Subscribe();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => b.Devices.DisconnectAsync(b.Id, Token));
        if (failure == "throw") Assert.Same(b.Port.Failure, error);
        if (failure == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        if (failure == "ignored") Assert.Contains("did not confirm disconnection", error.Message);
        Assert.False(events.Reader.TryRead(out _));
        var status = await b.Devices.GetStatusAsync(b.Id, Token);
        Assert.True(status.Connected);
        Assert.Equal(InstrumentLifecycleState.Faulted, status.Lifecycle);
        Assert.Equal(TemperatureStabilityState.Fault, status.Temperature!.State);
        Assert.Null(status.Temperature.Error);
        Assert.Null(status.Temperature.InToleranceFor);
        await b.TemperatureSession.StopDeviceAsync(Token);
        Assert.Equal(TemperatureStabilityState.Reached, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
    }

    [Fact]
    public async Task ConfirmedDisconnectPublishesUnknownFlagsAndReconnectStartsNewEvidence()
    {
        var b = new Bench(useStateClock: true);
        await b.Devices.ConnectAsync(b.Id, Token);
        b.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        using var events = b.Hub.Subscribe();
        await b.Devices.DisconnectAsync(b.Id, Token);
        Assert.True(events.Reader.TryRead(out var item));
        Assert.False(item.Device!.Connected);
        Assert.Equal(InstrumentLifecycleState.Disconnected, item.Device.Lifecycle);
        Assert.Equal(TemperatureStabilityState.Fault, item.Device.Temperature!.State);
        Assert.Null(item.Device.Temperature.Control);
        var reconnected = await b.Devices.ConnectAsync(b.Id, Token);
        Assert.Equal(TemperatureStabilityState.Reached, reconnected.Temperature!.State);
        Assert.Equal(TimeSpan.Zero, reconnected.Temperature.InToleranceFor);
    }

    [Fact]
    public async Task MovingDisconnectPreconditionCannotInvokeDriverOrFaultKnownRuntime()
    {
        var b = new Bench(useStateClock: true);
        await b.Devices.ConnectAsync(b.Id, Token);
        await b.Devices.WriteAsync(b.Id, b.Port.Definition.Target.Id, K(305), Token);
        b.Port.DisconnectFault = "throw";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Devices.DisconnectAsync(b.Id, Token));
        Assert.Contains("Confirm hold", error.Message);
        var status = await b.Devices.GetStatusAsync(b.Id, Token);
        Assert.Equal(InstrumentLifecycleState.Running, status.Lifecycle);
        Assert.Equal(TemperatureStabilityState.Ramping, status.Temperature!.State);
    }

    [Fact]
    public async Task ManualStatusAndRunWaitReadShareSessionEvidenceAndTypedNotifications()
    {
        var b = new Bench(useStateClock: true); using var events = b.Hub.Subscribe();
        var connected = await b.Devices.ConnectAsync(b.Id, Token);
        Assert.Equal(TemperatureStabilityState.Reached, connected.Temperature!.State);
        b.Clock.Advance(TimeSpan.FromSeconds(5)); var stable = await b.Devices.GetStatusAsync(b.Id, Token);
        Assert.Equal(TemperatureStabilityState.Stable, stable.Temperature!.State); Assert.Equal(TimeSpan.FromSeconds(5), stable.Temperature.InToleranceFor);
        Assert.Equal(K(0), stable.Temperature.Error); Assert.False(stable.Temperature.SoftwareRampActive);
        var observed = await b.Devices.ReadTemperatureStabilityAsync(b.Id, Token);
        Assert.Equal(stable.Temperature, observed.Runtime); Assert.Equal(stable.Values[b.Port.Definition.Readback.Id], observed.Snapshot.Readback);
        var states = new List<TemperatureRuntimeStatus>(); while (events.Reader.TryRead(out var item)) if (item.Device?.Temperature is { } state) states.Add(state);
        Assert.Equal(new[] { TemperatureStabilityState.Reached, TemperatureStabilityState.Stable, TemperatureStabilityState.Stable }, states.Select(s => s.State));
        Assert.Equal(stable.Temperature, states[^1]);
    }

    [Fact]
    public async Task OnlyChangedConfigurationResetsSharedClockAndDisconnectedStateHasUnknownFlags()
    {
        var b = new Bench(useStateClock: true); await b.Devices.ConnectAsync(b.Id, Token); b.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(), Token);
        await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(stableFor: TimeSpan.FromSeconds(5)), Token);
        Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.ReadTemperatureStabilityAsync(b.Id, Token)).Runtime!.State);
        await b.Devices.ConfigureTemperatureStabilityAsync(b.Id, new(K(0.2)), Token);
        Assert.Equal(TemperatureStabilityState.Reached, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        b.Clock.Advance(TimeSpan.FromSeconds(5)); Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        await b.Devices.DisconnectAsync(b.Id, Token); var disconnected = await b.Devices.GetStatusAsync(b.Id, Token);
        Assert.Equal(TemperatureStabilityState.Fault, disconnected.Temperature!.State); Assert.Null(disconnected.Temperature.Control);
        Assert.Null(disconnected.Temperature.Error); Assert.Null(disconnected.Temperature.InToleranceFor);
        Assert.Equal(TemperatureStabilityState.Reached, (await b.Devices.ConnectAsync(b.Id, Token)).Temperature!.State);
    }

    [Fact]
    public async Task SecondWaitReusesSharedStableEvidenceInsteadOfRequiringAnotherDuration()
    {
        var b = new Bench();
        var first = new WaitTemperatureStep(new("wait.first"), b.Id, new ConstantValue(K(300)), TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        var second = new WaitTemperatureStep(new("wait.second"), b.Id, new ConstantValue(K(300)), TemperatureWaitMode.Stable, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(25));
        await b.Runs.StartPlanAsync(new("Shared stable session", new SequenceStep(new("root"), [Settings(b, 0.08), first, second, b.Read()])), Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task FailedObservationInvalidatesStableEvidenceUntilConfirmedHold()
    {
        var b = new Bench(useStateClock: true); await b.Devices.ConnectAsync(b.Id, Token); b.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        b.Port.FailNextConfigurationRead = true;
        Assert.Same(b.Port.HoldReadFailure, await Assert.ThrowsAsync<IOException>(() => b.Devices.GetStatusAsync(b.Id, Token)));
        Assert.Equal(TemperatureStabilityState.Fault, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
        await b.TemperatureSession.StopDeviceAsync(Token); var stopped = await b.Devices.GetStatusAsync(b.Id, Token);
        Assert.Equal(TemperatureStabilityState.Reached, stopped.Temperature!.State); Assert.Equal(TimeSpan.Zero, stopped.Temperature.InToleranceFor);
        b.Clock.Advance(TimeSpan.FromSeconds(5)); Assert.Equal(TemperatureStabilityState.Stable, (await b.Devices.GetStatusAsync(b.Id, Token)).Temperature!.State);
    }

    [Fact]
    public async Task RampReportsFinalGoalAndSeparateSoftwareRateThenRestoresThemAfterPause()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var step = new SoftwareTemperatureRampStep(new("ramp"), b.Id, new ConstantValue(K(305)), Rate(7), enableControl: false);
        var run = b.Runs.StartPlanAsync(new("Runtime goal", step), Token);
        try
        {
            var running = await UntilRuntime(events, state => state.SoftwareRampActive);
            Assert.Equal(K(305), running.Temperature!.SoftwareTarget); Assert.Equal(Rate(7), running.Temperature.SoftwareRate);
            Assert.Equal(Rate(5), running.Values[b.Port.Definition.Rate.Id]);
            Assert.Equal(TemperatureStabilityState.Ramping, running.Temperature.State); Assert.Equal(K(5), running.Temperature.Error);
            Assert.NotEqual(K(305), running.Values[b.Port.Definition.Target.Id]);
            b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            var paused = await b.TemperatureSession.GetDeviceStatusAsync(Token, b.Runs.LatestDataSet!.Id);
            Assert.False(paused.Temperature!.SoftwareRampActive); Assert.Null(paused.Temperature.SoftwareRate);
            b.Runs.Resume(); var restored = await UntilRuntime(events, state => state.SoftwareRampActive);
            Assert.Equal(K(305), restored.Temperature!.SoftwareTarget); Assert.Equal(Rate(7), restored.Temperature.SoftwareRate);
            Assert.Equal(TemperatureStabilityState.Ramping, restored.Temperature.State);
        }
        finally { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        var completed = await b.Devices.GetStatusAsync(b.Id, Token);
        Assert.False(completed.Temperature!.SoftwareRampActive); Assert.Null(completed.Temperature.SoftwareRate);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectOrCompletedSoftwareRampClearsIntentAndNeverLeavesPhantomActivity(bool direct)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        await b.Runs.StartPlanAsync(new("Completed intent", new SoftwareTemperatureRampStep(new("ramp"), b.Id, new ConstantValue(K(300.0006)), direct ? null : Rate(7))), Token);
        var status = await b.Devices.GetStatusAsync(b.Id, Token); Assert.False(status.Temperature!.SoftwareRampActive);
        var states = new List<TemperatureRuntimeStatus>(); while (events.Reader.TryRead(out var item)) if (item.Device?.Temperature is { } state) states.Add(state);
        Assert.Equal(!direct, states.Any(state => state.SoftwareRampActive)); Assert.False(states[^1].SoftwareRampActive);
    }

    [Theory]
    [InlineData(TemperatureControlAction.ControlOff, TemperatureStabilityState.Cooling)]
    [InlineData(TemperatureControlAction.ControlOn, TemperatureStabilityState.Reached)]
    public async Task IndependentControlChangesUpdateSharedRuntimeWithoutChangingHardwareRate(TemperatureControlAction action, TemperatureStabilityState expected)
    {
        var b = new Bench(useStateClock: true); await b.Devices.ConnectAsync(b.Id, Token);
        await b.Devices.ConfigureTemperatureAsync(b.Id, TemperatureControlAction.ControlOff, null, Token);
        await b.Devices.ConfigureTemperatureAsync(b.Id, action, null, Token);
        var status = await b.Devices.GetStatusAsync(b.Id, Token); Assert.Equal(expected, status.Temperature!.State);
        Assert.Equal(Rate(5), status.Values[b.Port.Definition.Rate.Id]);
        if (action == TemperatureControlAction.ControlOff) { Assert.Null(status.Temperature.Error); Assert.Null(status.Temperature.InToleranceFor); }
    }

    private static async Task<DeviceStatus> UntilRuntime(NotificationSubscription events, Func<TemperatureRuntimeStatus, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var item = await events.Reader.ReadAsync(deadline.Token);
            if (item.Device?.Temperature is { } runtime && predicate(runtime)) return item.Device;
        }
    }
}

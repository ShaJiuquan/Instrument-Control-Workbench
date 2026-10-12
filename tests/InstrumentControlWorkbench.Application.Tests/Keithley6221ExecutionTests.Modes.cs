using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    private static K6221CurrentSourceSnapshot WithMode(K6221CurrentSourceSnapshot state, string damage)
    {
        if (!state.Connected) return state;
        var mode = damage == "absent" ? null : new Keithley6221ModeObservation(
            damage == "delta", damage == "pulse", damage == "differential", damage == "wave", damage == "sweep",
            damage == "unknown" ? null : damage == "running" ? (ushort)1032 : (ushort)1024, state.Timestamp);
        return new(state.Connected, state.OutputEnabled, state.Settings, state.Timestamp, mode);
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("unknown")]
    [InlineData("delta")]
    [InlineData("pulse")]
    [InlineData("differential")]
    [InlineData("wave")]
    [InlineData("sweep")]
    [InlineData("running")]
    public async Task HardwareUnknownOrArmedModeBlocksDcOperationsButAllowsOutputDiagnosis(string damage)
    {
        await using var b = new Bench(); b.Source.Kind = DriverKind.Hardware; b.Source.Observe = s => WithMode(s, damage);
        var session = new Keithley6221CurrentSourceSession(b.Source, b.Hub);
        var connected = await session.ConnectDeviceAsync(Token); Assert.False(connected.SafetyConfirmed); Assert.Equal(InstrumentLifecycleState.Faulted, connected.Lifecycle);
        Assert.Equal(new(0, PhysicalUnit.Dimensionless), await session.ReadParameterAsync(Keithley6221Parameters.Output.Id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadParameterAsync(Keithley6221Parameters.Current.Id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteParameterAsync(Keithley6221Parameters.Current.Id, new(1e-5, PhysicalUnit.Ampere), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteParameterAsync(Keithley6221Parameters.Output.Id, new(1, PhysicalUnit.Dimensionless), Token));
        Assert.Equal(0, b.Source.Configurations); Assert.Equal(0, b.Source.Enables);
        b.Source.Observe = null;
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("wave")]
    [InlineData("sweep")]
    public async Task OutputOffCannotConfirmStopOrPermitDisconnectUntilAutonomousModeIsCleared(string damage)
    {
        await using var b = new Bench(); b.Source.Kind = DriverKind.Hardware; b.Source.Observe = s => WithMode(s, damage);
        var session = new Keithley6221CurrentSourceSession(b.Source, b.Hub); await session.ConnectDeviceAsync(Token);
        using var events = b.Hub.Subscribe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopDeviceAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteParameterAsync(Keithley6221Parameters.Output.Id, new(0, PhysicalUnit.Dimensionless), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisconnectDeviceAsync(Token));
        Assert.Equal(3, b.Source.StopCalls); Assert.Equal(0, b.Source.Disconnections); Assert.False((await session.GetDeviceStatusAsync(Token)).SafetyConfirmed);
        while (events.Reader.TryRead(out var notification)) Assert.NotEqual(WorkbenchNotificationKind.SafetyStateChanged, notification.Kind);
        b.Source.OnStop = _ => { b.Source.Observe = null; return Task.CompletedTask; };
        await session.WriteParameterAsync(Keithley6221Parameters.Output.Id, new(0, PhysicalUnit.Dimensionless), Token);
        Assert.Equal(4, b.Source.StopCalls); var recovered = await session.GetDeviceStatusAsync(Token);
        Assert.True(recovered.SafetyConfirmed); Assert.Equal(InstrumentLifecycleState.Idle, recovered.Lifecycle);
        await session.WriteParameterAsync(Keithley6221Parameters.Output.Id, new(1, PhysicalUnit.Dimensionless), Token);
        Assert.Equal(1, b.Source.Enables); await session.DisconnectDeviceAsync(Token); Assert.Equal(1, b.Source.Disconnections);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeDriftAfterConfigurationOrEnableIsRejectedAndRequiresVerifiedRecovery(bool enable)
    {
        await using var b = new Bench(); b.Source.Kind = DriverKind.Hardware;
        var session = new Keithley6221CurrentSourceSession(b.Source, b.Hub); await session.ConnectDeviceAsync(Token);
        if (enable) b.Source.Observe = s => WithMode(s, s.OutputEnabled ? "wave" : "ready");
        else b.Source.OnConfigured = _ => { b.Source.Observe = s => WithMode(s, "sweep"); return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteParameterAsync(enable ? Keithley6221Parameters.Output.Id : Keithley6221Parameters.Current.Id,
            enable ? new(1, PhysicalUnit.Dimensionless) : new(1e-5, PhysicalUnit.Ampere), Token));
        Assert.Equal(InstrumentLifecycleState.Faulted, (await session.GetDeviceStatusAsync(Token)).Lifecycle);
        b.Source.Observe = null; b.Source.OnConfigured = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteParameterAsync(Keithley6221Parameters.Output.Id, new(1, PhysicalUnit.Dimensionless), Token));
        Assert.True((await session.StopDeviceAsync(Token)).SafetyConfirmed);
        await session.WriteParameterAsync(Keithley6221Parameters.Output.Id, new(1, PhysicalUnit.Dimensionless), Token);
        await session.StopDeviceAsync(Token);
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("wave")]
    [InlineData("sweep")]
    [InlineData("running")]
    public async Task ModeDriftDuringAcquisitionDiscardsRowAndRetainsAllClaimsUntilFreshStop(string damage)
    {
        await using var b = new Bench(); b.Source.Kind = DriverKind.Hardware;
        b.Meter.OnRead = _ => { b.Source.Observe = s => WithMode(s, damage); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(true), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id, new(0, PhysicalUnit.Ampere), Token));
        b.Source.Observe = null; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token);
        Assert.False(b.Runs.HasActiveRun); Assert.All(b.Registry.Devices, d => Assert.False(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        await b.Runs.StartPlanAsync(b.Plan(true), Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }
    [Fact]
    public async Task LegacyAbsentModeIsAcceptedOnlyForExplicitSimulationAndInvalidEvidenceNeverIs()
    {
        await using var b = new Bench(); b.Source.Observe = s => WithMode(s, "absent");
        await b.Runs.StartPlanAsync(b.Plan(false), Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
        b.Source.Observe = s => WithMode(s, "wave");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(false), Token));
            Assert.Null(b.Runs.LatestDataSet); Assert.True(b.Runs.HasActiveRun);
        }
        finally { b.Source.Observe = null; await b.Runs.RecoverSafetyAsync(Token); }
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("wave")]
    [InlineData("sweep")]
    public async Task UnknownHardwareModeAtRunPreparationDoesNotCreateDataOrEnableSource(string damage)
    {
        await using var b = new Bench(); b.Source.Kind = DriverKind.Hardware; b.Source.Observe = s => WithMode(s, damage);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(true), Token));
            Assert.Equal(0, b.Source.Configurations); Assert.Equal(0, b.Source.Enables); Assert.Null(b.Runs.LatestDataSet);
            Assert.True(b.Runs.HasActiveRun); Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        }
        finally { b.Source.Observe = null; await b.Runs.RecoverSafetyAsync(Token); }
        Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("wave")]
    [InlineData("sweep")]
    public async Task UnknownModeWhilePausedCannotRestoreOutputEvenWithUnchangedSettings(string damage)
    {
        await using var b = new Bench(); b.Source.Kind = DriverKind.Hardware; using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(false, new DelayStep(new("wait"), TimeSpan.FromSeconds(30))), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            var enables = b.Source.Enables; b.Source.Observe = s => WithMode(s, damage); b.Runs.Resume();
            await Assert.ThrowsAnyAsync<Exception>(() => running); Assert.Equal(enables, b.Source.Enables); Assert.True(b.Runs.HasActiveRun);
            Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        }
        finally
        {
            b.Source.Observe = null; b.Runs.Abort();
            try { await running; } catch (InvalidOperationException) { } catch (AggregateException) { }
            if (b.Runs.HasActiveRun) await b.Runs.RecoverSafetyAsync(Token);
        }
        Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task SimulatorReportsExplicitFreshDcEvidenceAndSuccessfulStopOrdering()
    {
        await using var b = new Bench(); var session = new Keithley6221CurrentSourceSession(b.Source, b.Hub); await session.ConnectDeviceAsync(Token);
        var state = await b.Source.GetSnapshotAsync(Token); Assert.True(state.Mode!.IsDcReady); Assert.Equal(state.Timestamp, state.Mode.CapturedAt);
        using var events = b.Hub.Subscribe(); var stopped = await session.StopDeviceAsync(Token); Assert.True(stopped.SafetyConfirmed);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkbenchNotificationKind.DeviceStateChanged, (await events.Reader.ReadAsync(deadline.Token)).Kind);
        Assert.Equal(WorkbenchNotificationKind.SafetyStateChanged, (await events.Reader.ReadAsync(deadline.Token)).Kind);
    }
}

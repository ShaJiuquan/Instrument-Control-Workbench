using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Instruments;

public sealed partial class InstrumentManager : IDeviceSession
{
    private readonly ISourceMeterPort sourceMeter;
    private readonly NotificationHub notifications;
    private readonly InstrumentStateMachine stateMachine = new();
    private readonly InstrumentOperationGate operations;
    private InstrumentLifecycleState lifecycle = InstrumentLifecycleState.Disconnected;

    public InstrumentManager(ISourceMeterPort sourceMeter, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(sourceMeter);
        ArgumentNullException.ThrowIfNull(notifications);
        if (sourceMeter.Descriptor.Id != sourceMeter.Id || sourceMeter.Descriptor.StopPolicy != DeviceStopPolicy.OutputOff)
            throw new ArgumentException("Source-meter identity and output-off policy must match its descriptor.", nameof(sourceMeter));
        this.sourceMeter = sourceMeter;
        this.notifications = notifications;
        operations = new(sourceMeter.Id);
    }

    public InstrumentId InstrumentId => sourceMeter.Id;

    public DeviceDescriptor Descriptor => sourceMeter.Descriptor;
    internal Task ClaimRunAsync(RunId owner, CancellationToken token) => operations.ClaimAsync(owner, token);
    internal Task ReleaseRunAsync(RunId owner, CancellationToken token) => operations.ReleaseAsync(owner, token);
    Task IDeviceSession.ClaimRunAsync(RunId owner, CancellationToken token) => ClaimRunAsync(owner, token);
    Task IDeviceSession.ReleaseRunAsync(RunId owner, CancellationToken token) => ReleaseRunAsync(owner, token);
    private Task EnterOperationAsync(CancellationToken token, RunId? owner) => operations.EnterAsync(token, owner);

    public async Task<InstrumentParameterSnapshot> CaptureParametersAsync(CancellationToken cancellationToken, RunId? owner = null)
    {
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            var voltageSource = snapshot.Source.Function == SourceFunction.Voltage;
            var level = voltageSource ? Keithley2450Parameters.SourceVoltage : Keithley2450Parameters.SourceCurrent;
            var compliance = voltageSource ? Keithley2450Parameters.CurrentCompliance : Keithley2450Parameters.VoltageCompliance;
            return new InstrumentParameterSnapshot(snapshot.InstrumentId, snapshot.Identifier, DateTimeOffset.UtcNow,
                [new(level, new(snapshot.Source.Level, level.Unit)),
                 new(compliance, new(snapshot.Source.Compliance, compliance.Unit)),
                 new(Keithley2450Parameters.Nplc, new(snapshot.Sense.Nplc, PhysicalUnit.Dimensionless))],
                new Dictionary<string, string>
                {
                    ["source_function"] = snapshot.Source.Function.ToString(),
                    ["sense_function"] = snapshot.Sense.Function.ToString(),
                    ["sensing_mode"] = snapshot.Sense.Mode.ToString(),
                    ["terminals"] = snapshot.Terminals.ToString(),
                    ["output_enabled"] = snapshot.OutputEnabled.ToString(),
                });
        }
        finally
        {
            operations.Exit();
        }
    }

    public async Task<SourceMeterSnapshot> ConnectAsync(CancellationToken cancellationToken, RunId? owner = null)
    {
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            if (lifecycle == InstrumentLifecycleState.Idle)
            {
                var current = await sourceMeter.GetSnapshotAsync(cancellationToken);
                if (current.Connected)
                {
                    return await PublishSnapshotAsync(current with { Lifecycle = lifecycle }, cancellationToken);
                }
                Transition(InstrumentLifecycleEvent.Disconnected);
            }

            Transition(InstrumentLifecycleEvent.ConnectRequested);
            await PublishLogAsync($"Connecting {sourceMeter.DisplayName}.", cancellationToken);
            var snapshot = await sourceMeter.ConnectAsync(cancellationToken);
            Transition(InstrumentLifecycleEvent.Connected);
            return await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
        }
        catch
        {
            Transition(InstrumentLifecycleEvent.FaultDetected);
            throw;
        }
        finally
        {
            operations.Exit();
        }
    }

    public async Task<SourceMeterSnapshot> ConfigureAsync(
        SourceSetpoint source,
        SenseConfiguration sense,
        CancellationToken cancellationToken, RunId? owner = null)
    {
        Keithley2450Limits.Validate(source);
        Keithley2450Limits.Validate(sense);
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            if ((await ReadSourceSnapshotAsync(cancellationToken)).OutputEnabled)
                throw new InvalidOperationException("Confirm output off before preparing source mode and configuration.");
            try
            {
                Transition(InstrumentLifecycleEvent.ConfigureRequested);
                await sourceMeter.ConfigureSourceAsync(source, cancellationToken);
                await sourceMeter.ConfigureSenseAsync(sense, cancellationToken);
                Transition(InstrumentLifecycleEvent.Configured);
                var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
                await PublishLogAsync($"Configured source {source.Level:0.###} {source.LevelUnit}, compliance {source.Compliance:0.###E+0} {source.ComplianceUnit}.", cancellationToken);
                return await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
            }
            catch
            {
                Transition(InstrumentLifecycleEvent.FaultDetected);
                throw;
            }
        }
        finally
        {
            operations.Exit();
        }
    }

    public async Task EnterRunAsync(CancellationToken cancellationToken, RunId? owner = null)
    {
        if (owner is null) throw new InvalidOperationException("Exclusive run resources are required before enabling source output.");
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            Transition(InstrumentLifecycleEvent.RunRequested);
            await sourceMeter.EnableOutputAsync(cancellationToken);
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
        }
        catch
        {
            Transition(InstrumentLifecycleEvent.FaultDetected);
            throw;
        }
        finally
        {
            operations.Exit();
        }
    }

    public async Task<MeasurementValue> SetAndReadAsync(SourceSetpoint setpoint, CancellationToken cancellationToken, RunId? owner = null)
    {
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            await sourceMeter.ConfigureSourceAsync(setpoint, cancellationToken);
            var value = await sourceMeter.ReadAsync(cancellationToken);
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
            return value;
        }
        catch
        {
            Transition(InstrumentLifecycleEvent.FaultDetected);
            throw;
        }
        finally
        {
            operations.Exit();
        }
    }

    public async Task SafeStopAsync(string reason, CancellationToken cancellationToken, RunId? owner = null)
    {
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            if (lifecycle == InstrumentLifecycleState.Running)
            {
                Transition(InstrumentLifecycleEvent.StopRequested);
            }

            await sourceMeter.SafeStopAsync(cancellationToken);
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            if (!snapshot.Connected && Descriptor.DriverKind == DriverKind.Hardware)
                throw new InvalidOperationException("Disconnected hardware cannot confirm output off; reconnect under the current owner before recovery.");
            if (snapshot.OutputEnabled) throw new InvalidOperationException("Safe stop did not confirm output off; the instrument remains reserved.");
            if (lifecycle is InstrumentLifecycleState.Stopping or InstrumentLifecycleState.Faulted)
            {
                Transition(InstrumentLifecycleEvent.SafeStateReached);
            }
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
            await notifications.PublishAsync(
                new WorkbenchNotification(
                    WorkbenchNotificationKind.SafetyStateChanged,
                    $"Safe stop completed: {reason}",
                    sourceMeter.Id),
                cancellationToken);
        }
        catch
        {
            Transition(InstrumentLifecycleEvent.FaultDetected);
            throw;
        }
        finally
        {
            operations.Exit();
        }
    }

    public async Task CheckHeartbeatAsync(TimeSpan ttl, CancellationToken cancellationToken, RunId? owner = null)
    {
        await EnterOperationAsync(cancellationToken, owner);
        try
        {
            var snapshot = await sourceMeter.HeartbeatAsync(cancellationToken);
            if (DateTimeOffset.Now - snapshot.LastHeartbeat > ttl)
            {
                Transition(InstrumentLifecycleEvent.HeartbeatExpired);
                await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
            }
        }
        finally { operations.Exit(); }
    }

    private void Transition(InstrumentLifecycleEvent evt)
    {
        lifecycle = stateMachine.Apply(lifecycle, evt);
    }

    private async Task<SourceMeterSnapshot> PublishSnapshotAsync(SourceMeterSnapshot snapshot, CancellationToken cancellationToken)
    {
        await notifications.PublishAsync(
            new WorkbenchNotification(
                WorkbenchNotificationKind.InstrumentStateChanged,
                $"{snapshot.Identifier}: {snapshot.Lifecycle}",
                snapshot.InstrumentId,
                snapshot, Device: ToDeviceStatus(snapshot)),
            cancellationToken);
        return snapshot;
    }

    private Task PublishLogAsync(string message, CancellationToken cancellationToken)
    {
        return notifications.PublishAsync(
            new WorkbenchNotification(
                WorkbenchNotificationKind.LogAppended,
                message,
                sourceMeter.Id),
            cancellationToken).AsTask();
    }
}

using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Instruments;

public sealed class InstrumentManager
{
    private readonly ISourceMeterPort sourceMeter;
    private readonly NotificationHub notifications;
    private readonly InstrumentStateMachine stateMachine = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private InstrumentLifecycleState lifecycle = InstrumentLifecycleState.Disconnected;

    public InstrumentManager(ISourceMeterPort sourceMeter, NotificationHub notifications)
    {
        this.sourceMeter = sourceMeter;
        this.notifications = notifications;
    }

    public InstrumentId InstrumentId => sourceMeter.Id;

    public async Task<InstrumentParameterSnapshot> CaptureParametersAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
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
            operationGate.Release();
        }
    }

    public async Task<SourceMeterSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
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
            await PublishLogAsync("Connecting mock Keithley 2450.", cancellationToken);
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
            operationGate.Release();
        }
    }

    public async Task<SourceMeterSnapshot> ConfigureAsync(
        SourceSetpoint source,
        SenseConfiguration sense,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
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
        finally
        {
            operationGate.Release();
        }
    }

    public async Task EnterRunAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            Transition(InstrumentLifecycleEvent.RunRequested);
            await sourceMeter.EnableOutputAsync(cancellationToken);
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<MeasurementValue> SetAndReadAsync(SourceSetpoint setpoint, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            await sourceMeter.ConfigureSourceAsync(setpoint, cancellationToken);
            var value = await sourceMeter.ReadAsync(cancellationToken);
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
            return value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SafeStopAsync(string reason, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(CancellationToken.None);
        try
        {
            if (lifecycle == InstrumentLifecycleState.Running)
            {
                Transition(InstrumentLifecycleEvent.StopRequested);
            }

            await sourceMeter.SafeStopAsync(cancellationToken);
            if (lifecycle is InstrumentLifecycleState.Stopping or InstrumentLifecycleState.Faulted)
            {
                Transition(InstrumentLifecycleEvent.SafeStateReached);
            }
            var snapshot = await sourceMeter.GetSnapshotAsync(cancellationToken);
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
            await notifications.PublishAsync(
                new WorkbenchNotification(
                    WorkbenchNotificationKind.SafetyStateChanged,
                    $"Safe stop completed: {reason}",
                    sourceMeter.Id),
                cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task CheckHeartbeatAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        var snapshot = await sourceMeter.HeartbeatAsync(cancellationToken);
        if (DateTimeOffset.Now - snapshot.LastHeartbeat > ttl)
        {
            Transition(InstrumentLifecycleEvent.HeartbeatExpired);
            await PublishSnapshotAsync(snapshot with { Lifecycle = lifecycle }, cancellationToken);
        }
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
                snapshot),
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

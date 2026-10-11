using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class Keithley2182MeterSession : IDeviceSession
{
    private readonly IK2182MeterPort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;
    public Keithley2182MeterSession(IK2182MeterPort port, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(port); ArgumentNullException.ThrowIfNull(notifications);
        if (port.Descriptor.Model != "K2182" || port.Descriptor.StopPolicy != DeviceStopPolicy.AbortAcquisition || !port.Descriptor.Parameters.SequenceEqual(Keithley2182Parameters.All))
            throw new ArgumentException("K2182 session requires its passive-meter profile.", nameof(port));
        this.port = port; this.notifications = notifications; operations = new(port.Descriptor.Id);
    }
    public DeviceDescriptor Descriptor => port.Descriptor;
    public Task ClaimRunAsync(RunId owner, CancellationToken token) => operations.ClaimAsync(owner, token);
    public Task ReleaseRunAsync(RunId owner, CancellationToken token) => operations.ReleaseAsync(owner, token);
    public async Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected) snapshot = await port.ConnectAsync(token);
            if (!snapshot.Connected) throw new InvalidOperationException($"{Descriptor.Id} did not confirm connection.");
            faulted = false; return await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<DeviceStatus> GetDeviceStatusAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try { return await PublishAsync(await port.GetSnapshotAsync(token), token); }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task DisconnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            if ((await port.GetSnapshotAsync(token)).Acquiring) throw new InvalidOperationException("Abort acquisition before disconnecting the voltage meter.");
            await port.DisconnectAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (snapshot.Connected) throw new InvalidOperationException("Voltage meter did not confirm disconnection.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => (await ReadObservationAsync(parameter, token, owner)).Value;
    public Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => ReadObservationAsync(parameter, token, owner, null);
    public async Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner, int? requiredInput, bool classifyReadFailure = false)
    {
        var descriptor = Descriptor.GetParameter(parameter);
        if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException($"{parameter} is write-only.");
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Acquiring) throw new InvalidOperationException("Voltage meter is disconnected or already acquiring.");
            if (parameter != Keithley2182Parameters.Voltage.Id)
            {
                if (!before.Settings!.CaptureParameters().TryGetValue(parameter, out var value))
                    throw new InvalidOperationException("Manual span is absent while autorange is enabled.");
                return new(value, before.Timestamp);
            }
            if (requiredInput is { } input && before.Settings!.Channel != input) throw new InvalidOperationException("Meter input differs from the frozen sample wiring.");
            var started = DateTimeOffset.UtcNow;
            K2182VoltageReading reading;
            try { reading = await port.ReadAsync(token); }
            catch (InstrumentReadException error) when (classifyReadFailure && error.Device == Descriptor.Id)
            { throw new MeterAcquisitionException(error, before.Settings!, started, DateTimeOffset.UtcNow); }
            token.ThrowIfCancellationRequested();
            var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.Acquiring || after.Settings != before.Settings)
                throw new InvalidOperationException("Voltage meter configuration changed during acquisition; reading discarded.");
            descriptor.Validate(reading.Voltage);
            if (Math.Abs(reading.Voltage.Value) > Keithley2182Settings.MaximumInputVoltage(before.Settings!.Channel) || reading.CapturedAt > after.Timestamp || reading.CapturedAt < before.Timestamp)
                throw new InvalidOperationException("Voltage or acquisition timestamp is outside the observed input/frame boundary.");
            await PublishAsync(after, token);
            return new(reading.Voltage, reading.CapturedAt, MeterConfiguration: before.Settings);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Keithley2182Parameters.ValidateWrite(parameter, value);
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Acquiring) throw new InvalidOperationException("Voltage meter must be connected and idle before configuration.");
            var requested = before.Settings!.WithParameter(parameter, value);
            if (requested != before.Settings) await port.ConfigureAsync(requested, token);
            var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.Acquiring || after.Settings != requested) throw new InvalidOperationException("Voltage meter settings did not confirm.");
            await PublishAsync(after, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            await port.AbortAcquisitionAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (snapshot.Acquiring || !snapshot.Connected && Descriptor.DriverKind == DriverKind.Hardware)
                throw new InvalidOperationException("Voltage meter did not confirm acquisition idle; ownership must be retained.");
            faulted = false; var status = await PublishAsync(snapshot, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, $"{Descriptor.DisplayName}: acquisition idle confirmed.", Descriptor.Id), token);
            return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task<Keithley2182Settings> CaptureResumeSettingsAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Acquiring) throw new InvalidOperationException("Voltage meter must be connected and idle for pause/resume confirmation.");
            return snapshot.Settings!;
        }
        finally { operations.Exit(); }
    }
    private async Task<DeviceStatus> PublishAsync(K2182MeterSnapshot snapshot, CancellationToken token)
    {
        var state = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted : snapshot.Acquiring ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var status = new DeviceStatus(Descriptor, state, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.Acquiring,
            snapshot.Timestamp, snapshot.Connected ? snapshot.Settings!.CaptureParameters() : new Dictionary<ParameterId, Quantity>());
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, $"{Descriptor.DisplayName}: {state}", Descriptor.Id, Device: status), token);
        return status;
    }
}

// Only the ReadAsync boundary can produce this envelope. Snapshot, settings,
// validation, cancellation and route failures cannot enter the retry path.
internal sealed class MeterAcquisitionException(InstrumentReadException error, Keithley2182Settings settings, DateTimeOffset startedAt, DateTimeOffset failedAt)
    : Exception(error.Message, error)
{
    public InstrumentReadException Failure { get; } = error;
    public Keithley2182Settings Settings { get; } = settings;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset FailedAt { get; } = failedAt;
}

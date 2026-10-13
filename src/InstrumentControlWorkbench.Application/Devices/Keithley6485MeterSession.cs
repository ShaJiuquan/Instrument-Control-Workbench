using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class Keithley6485MeterSession : IDeviceSession
{
    private readonly IK6485MeterPort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;
    public Keithley6485MeterSession(IK6485MeterPort port, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(port); ArgumentNullException.ThrowIfNull(notifications);
        if (port.Descriptor.Model != "K6485" || port.Descriptor.StopPolicy != DeviceStopPolicy.AbortAcquisition || !port.Descriptor.Parameters.SequenceEqual(Keithley6485Parameters.All))
            throw new ArgumentException("K6485 session requires its passive-meter profile.", nameof(port));
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
            if ((await port.GetSnapshotAsync(token)).Acquiring) throw new InvalidOperationException("Abort acquisition before disconnecting the current meter.");
            await port.DisconnectAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (snapshot.Connected) throw new InvalidOperationException("Current meter did not confirm disconnection.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => (await ReadObservationAsync(parameter, token, owner)).Value;
    public Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => ReadCurrentObservationAsync(parameter, token, owner);
    private async Task<ParameterObservation> ReadCurrentObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner)
    {
        var descriptor = Descriptor.GetParameter(parameter);
        if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException($"{parameter} is write-only.");
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Acquiring) throw new InvalidOperationException("Current meter is disconnected or already acquiring.");
            if (parameter != Keithley6485Parameters.Current.Id)
            {
                if (!before.Settings!.CaptureParameters().TryGetValue(parameter, out var value))
                    throw new InvalidOperationException("Manual span is absent while autorange is enabled.");
                return new(value, before.Timestamp);
            }
            if (before.Settings!.ZeroCheck) throw new InvalidOperationException("Disable K6485 zero check explicitly before measuring sample current.");
            var reading = await port.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.Acquiring || after.Settings != before.Settings)
                throw new InvalidOperationException("Current meter configuration changed during acquisition; reading discarded.");
            descriptor.Validate(reading.Current);
            if (before.Settings.ManualRange is { } range && Math.Abs(reading.Current.Value) > range.Value * 1.05)
                throw new InvalidOperationException("Current exceeds the observed manual span; reading discarded.");
            if (reading.CapturedAt > after.Timestamp || reading.CapturedAt < before.Timestamp)
                throw new InvalidOperationException("Current or acquisition timestamp is outside the observed input/frame boundary.");
            await PublishAsync(after, token);
            return new(reading.Current, reading.CapturedAt, PicoammeterConfiguration: before.Settings);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Keithley6485Parameters.ValidateWrite(parameter, value);
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Acquiring) throw new InvalidOperationException("Current meter must be connected and idle before configuration.");
            var requested = before.Settings!.WithParameter(parameter, value);
            if (requested != before.Settings) await port.ConfigureAsync(requested, token);
            var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.Acquiring || after.Settings != requested) throw new InvalidOperationException("Current meter settings did not confirm.");
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
                throw new InvalidOperationException("Current meter did not confirm acquisition idle; ownership must be retained.");
            faulted = false; var status = await PublishAsync(snapshot, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, $"{Descriptor.DisplayName}: acquisition idle confirmed.", Descriptor.Id), token);
            return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task<Keithley6485Settings> CaptureResumeSettingsAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Acquiring) throw new InvalidOperationException("Current meter must be connected and idle for pause/resume confirmation.");
            return snapshot.Settings!;
        }
        finally { operations.Exit(); }
    }
    private async Task<DeviceStatus> PublishAsync(K6485MeterSnapshot snapshot, CancellationToken token)
    {
        var state = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted : snapshot.Acquiring ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var status = new DeviceStatus(Descriptor, state, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.Acquiring,
            snapshot.Timestamp, snapshot.Connected ? snapshot.Settings!.CaptureParameters() : new Dictionary<ParameterId, Quantity>());
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, $"{Descriptor.DisplayName}: {state}", Descriptor.Id, Device: status), token);
        return status;
    }
}


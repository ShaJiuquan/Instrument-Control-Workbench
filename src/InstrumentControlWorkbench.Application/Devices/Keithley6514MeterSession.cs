using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class Keithley6514MeterSession : IDeviceSession
{
    private readonly IK6514MeterPort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;
    public Keithley6514MeterSession(IK6514MeterPort port, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(port); ArgumentNullException.ThrowIfNull(notifications);
        if (port.Descriptor.Model != "K6514" || port.Descriptor.StopPolicy != DeviceStopPolicy.AbortAcquisition || !port.Descriptor.Parameters.SequenceEqual(Keithley6514Parameters.All))
            throw new ArgumentException("K6514 session requires its electrometer profile.", nameof(port));
        if (port.Descriptor.DriverKind != DriverKind.Simulation) throw new NotSupportedException("K6514 hardware requires verified intrinsic ohms excitation/guard stop evidence; only the simulation profile is implemented.");
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
            if ((await port.GetSnapshotAsync(token)).Acquiring) throw new InvalidOperationException("Abort acquisition before disconnecting the electrometer.");
            await port.DisconnectAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (snapshot.Connected) throw new InvalidOperationException("Electrometer did not confirm disconnection.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => (await ReadObservationAsync(parameter, token, owner)).Value;
    public Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => ReadElectrometerObservationAsync(parameter, token, owner);
    private async Task<ParameterObservation> ReadElectrometerObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner)
    {
        var descriptor = Descriptor.GetParameter(parameter);
        if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException($"{parameter} is write-only.");
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Acquiring) throw new InvalidOperationException("Electrometer is disconnected or already acquiring.");
            var function = Keithley6514Parameters.ReadingFunction(parameter);
            if (function is null)
            {
                if (!before.Settings!.CaptureParameters().TryGetValue(parameter, out var value))
                    throw new InvalidOperationException("Electrometer snapshot lacks its declared configuration register.");
                return new(value, before.Timestamp);
            }
            if (before.Settings!.Function != function.Value) throw new InvalidOperationException("Select the matching K6514 function explicitly before acquisition.");
            if (before.Settings.ZeroCheck) throw new InvalidOperationException("Disable K6514 zero check explicitly before measuring the sample.");
            var reading = await port.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.Acquiring || after.Settings != before.Settings)
                throw new InvalidOperationException("Electrometer configuration changed during acquisition; reading discarded.");
            if (reading.Function != function.Value) throw new InvalidOperationException("Returned electrometer function differs from the requested reading.");
            descriptor.Validate(reading.Value);
            before.Settings.ValidateReading(function.Value, reading.Value);
            if (reading.CapturedAt > after.Timestamp || reading.CapturedAt < before.Timestamp)
                throw new InvalidOperationException("Acquisition timestamp is outside the observed input/frame boundary.");
            await PublishAsync(after, token);
            return new(reading.Value, reading.CapturedAt, ElectrometerConfiguration: before.Settings);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Keithley6514Parameters.ValidateWrite(parameter, value);
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Acquiring) throw new InvalidOperationException("Electrometer must be connected and idle before configuration.");
            if (parameter == Keithley6514Parameters.Function.Id)
            {
                var expected = before.Settings!.SelectFunction((ElectrometerFunction)(int)value.Value);
                if (expected != before.Settings) await port.SelectFunctionAsync(expected.Function, token);
                var selected = await port.GetSnapshotAsync(token);
                if (!selected.Connected || selected.Acquiring || selected.Settings != expected) throw new InvalidOperationException("Function selection changed unrequested electrometer registers or did not confirm.");
                await PublishAsync(selected, token); return;
            }
            var requested = before.Settings!.WithParameter(parameter, value);
            if (requested != before.Settings) await port.ConfigureAsync(requested, token);
            var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.Acquiring || after.Settings != requested) throw new InvalidOperationException("Electrometer settings did not confirm.");
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
                throw new InvalidOperationException("Electrometer did not confirm acquisition idle; ownership must be retained.");
            faulted = false; var status = await PublishAsync(snapshot, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, $"{Descriptor.DisplayName}: acquisition idle confirmed.", Descriptor.Id), token);
            return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task<Keithley6514Settings> CaptureResumeSettingsAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Acquiring) throw new InvalidOperationException("Electrometer must be connected and idle for pause/resume confirmation.");
            return snapshot.Settings!;
        }
        finally { operations.Exit(); }
    }
    private async Task<DeviceStatus> PublishAsync(K6514MeterSnapshot snapshot, CancellationToken token)
    {
        var state = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted : snapshot.Acquiring ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var status = new DeviceStatus(Descriptor, state, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.Acquiring,
            snapshot.Timestamp, snapshot.Connected ? snapshot.Settings!.CaptureParameters() : new Dictionary<ParameterId, Quantity>());
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, $"{Descriptor.DisplayName}: {state}", Descriptor.Id, Device: status), token);
        return status;
    }
}


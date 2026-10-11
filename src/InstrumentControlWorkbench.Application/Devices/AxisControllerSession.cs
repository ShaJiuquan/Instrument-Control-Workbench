using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class AxisControllerSession : IDeviceSession
{
    private readonly IAxisControllerPort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;
    private readonly TimeProvider stateClock;
    private readonly long stateOrigin;

    public AxisControllerSession(IAxisControllerPort port, NotificationHub notifications, TimeProvider? stateClock = null)
    {
        if (port.Definition.SupportsSoftwareTemperatureRamp && port is not ITemperatureControlPort)
            throw new ArgumentException("Temperature ramp capability requires a temperature-control port.", nameof(port));
        this.port = port;
        this.notifications = notifications;
        operations = new(port.Definition.Device.Id);
        this.stateClock = stateClock ?? TimeProvider.System; stateOrigin = this.stateClock.GetTimestamp();
    }

    public AxisControllerDefinition Definition => port.Definition;
    public DeviceDescriptor Descriptor => port.Definition.Device;
    public Task ClaimRunAsync(RunId owner, CancellationToken token) => operations.ClaimAsync(owner, token);
    public Task ReleaseRunAsync(RunId owner, CancellationToken token) => operations.ReleaseAsync(owner, token);

    public async Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var current = await port.GetSnapshotAsync(token);
            if (!current.Connected) ResetTemperatureRuntime();
            var snapshot = current.Connected ? current : await port.ConnectAsync(token);
            faulted = false;
            return await PublishAsync(snapshot, token);
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }

    public async Task<DeviceStatus> GetDeviceStatusAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try { return await PublishAsync(await port.GetSnapshotAsync(token), token); }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }

    public async Task DisconnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token);
            if (snapshot.Moving) throw new InvalidOperationException("Confirm hold before disconnecting a moving controller.");
            try
            {
                await port.DisconnectAsync(token);
                var disconnected = await port.GetSnapshotAsync(token);
                if (disconnected.Connected) throw new InvalidOperationException($"{Descriptor.Id} did not confirm disconnection.");
                ResetTemperatureRuntime();
                await PublishAsync(disconnected, token);
            }
            catch { faulted = true; ResetTemperatureRuntime(); throw; }
        }
        finally { operations.Exit(); }
    }

    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
    {
        var descriptor = Descriptor.GetParameter(parameter);
        if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException($"{parameter} is write-only.");
        var status = await GetDeviceStatusAsync(token, owner);
        if (!status.Connected) throw new InvalidOperationException($"{Descriptor.Id} is disconnected.");
        return status.Values[parameter];
    }

    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Descriptor.GetParameter(parameter).ValidateWrite(value);
        await operations.EnterAsync(token, owner);
        try
        {
            if (parameter == port.Definition.Target.Id) await port.SetTargetAsync(value, token);
            else if (parameter == port.Definition.Rate.Id)
            {
                if (Definition.SupportsSoftwareTemperatureRamp) await ((ITemperatureControlPort)port).SetHardwareRampAsync(value, token);
                else await port.SetRateAsync(value, token);
            }
            else throw new NotSupportedException($"{Descriptor.Id} cannot write {parameter}.");
            var snapshot = await port.GetSnapshotAsync(token);
            if (Definition.SupportsSoftwareTemperatureRamp && parameter == Definition.Rate.Id &&
                (!snapshot.Connected || snapshot.Rate != value || await ((ITemperatureControlPort)port).ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(true, true)))
                throw new InvalidOperationException("Hardware temperature ramp or control configuration did not confirm.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }

    public async Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            ResetTemperatureRuntime();
            var temperature = Definition.SupportsSoftwareTemperatureRamp ? (ITemperatureControlPort)port : null;
            TemperatureControlConfiguration? configuration = null;
            Exception? observationError = null;
            if (temperature is not null)
            {
                try { configuration = await temperature.ReadControlConfigurationAsync(token); }
                catch (Exception error) { observationError = error; }
            }
            // An unreadable control flag cannot suppress the emergency hold.
            // It still prevents safety confirmation and release afterwards.
            try { await port.HoldAsync(token); }
            catch (Exception holdError) when (observationError is not null)
            { throw new AggregateException("Temperature flags could not be observed and hold failed.", observationError, holdError); }
            if (observationError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationError).Throw();
            var snapshot = await port.GetSnapshotAsync(token);
            if ((!snapshot.Connected && Descriptor.DriverKind == DriverKind.Hardware) || snapshot.Moving) throw new InvalidOperationException($"{Descriptor.Id} did not confirm its hold policy.");
            if (temperature is not null && (snapshot.Target != snapshot.Readback ||
                await temperature.ReadControlConfigurationAsync(token) != configuration))
                throw new InvalidOperationException($"{Descriptor.Id}: temperature hold changed control flags or did not park at live readback.");
            faulted = false;
            var status = await PublishAsync(snapshot, token, temperature is not null && configuration is not null ? new(snapshot, configuration, stabilitySettings) : null);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged,
                $"{Descriptor.DisplayName}: current position held; no automatic return to zero.", Descriptor.Id), token);
            return status;
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }

    private async Task<DeviceStatus> PublishAsync(AxisControllerSnapshot snapshot, CancellationToken token, TemperatureStabilityObservation? observed = null)
    {
        TemperatureRuntimeStatus? runtime = null;
        if (Definition.SupportsSoftwareTemperatureRamp)
        {
            if (snapshot.Connected)
            {
                observed ??= await ObserveTemperatureAsync(token);
                snapshot = observed.Snapshot; runtime = DeriveTemperatureRuntime(observed);
            }
            else { ResetTemperatureRuntime(); runtime = new(TemperatureStabilityState.Fault, null, null, stabilitySettings, null, null); }
        }
        var state = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted :
            snapshot.Moving ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var status = new DeviceStatus(Descriptor, state, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.Moving,
            snapshot.Timestamp, new Dictionary<ParameterId, Quantity>
            {
                [port.Definition.Target.Id] = snapshot.Target,
                [port.Definition.Readback.Id] = snapshot.Readback,
                [port.Definition.Rate.Id] = snapshot.Rate,
            }, runtime);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged,
            $"{Descriptor.DisplayName}: {state}", Descriptor.Id, Device: status), token);
        return status;
    }
}

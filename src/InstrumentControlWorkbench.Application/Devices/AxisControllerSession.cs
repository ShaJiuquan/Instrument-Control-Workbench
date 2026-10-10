using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class AxisControllerSession : IDeviceSession
{
    private readonly IAxisControllerPort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;

    public AxisControllerSession(IAxisControllerPort port, NotificationHub notifications)
    {
        this.port = port;
        this.notifications = notifications;
        operations = new(port.Definition.Device.Id);
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
            var snapshot = current.Connected ? current : await port.ConnectAsync(token);
            faulted = false;
            return await PublishAsync(snapshot, token);
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
            var snapshot = await port.GetSnapshotAsync(token);
            if (snapshot.Moving) throw new InvalidOperationException("Confirm hold before disconnecting a moving controller.");
            await port.DisconnectAsync(token);
            await PublishAsync(await port.GetSnapshotAsync(token), token);
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
            else if (parameter == port.Definition.Rate.Id) await port.SetRateAsync(value, token);
            else throw new NotSupportedException($"{Descriptor.Id} cannot write {parameter}.");
            await PublishAsync(await port.GetSnapshotAsync(token), token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    public async Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            await port.HoldAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if ((!snapshot.Connected && Descriptor.DriverKind == DriverKind.Hardware) || snapshot.Moving) throw new InvalidOperationException($"{Descriptor.Id} did not confirm its hold policy.");
            faulted = false;
            var status = await PublishAsync(snapshot, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged,
                $"{Descriptor.DisplayName}: current position held; no automatic return to zero.", Descriptor.Id), token);
            return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    private async Task<DeviceStatus> PublishAsync(AxisControllerSnapshot snapshot, CancellationToken token)
    {
        var state = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted :
            snapshot.Moving ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var status = new DeviceStatus(Descriptor, state, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.Moving,
            snapshot.Timestamp, new Dictionary<ParameterId, Quantity>
            {
                [port.Definition.Target.Id] = snapshot.Target,
                [port.Definition.Readback.Id] = snapshot.Readback,
                [port.Definition.Rate.Id] = snapshot.Rate,
            });
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged,
            $"{Descriptor.DisplayName}: {state}", Descriptor.Id, Device: status), token);
        return status;
    }
}

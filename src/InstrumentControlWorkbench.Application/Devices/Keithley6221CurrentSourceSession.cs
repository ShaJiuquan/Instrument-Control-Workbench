using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class Keithley6221CurrentSourceSession : IDeviceSession
{
    private readonly IK6221CurrentSourcePort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;
    public Keithley6221CurrentSourceSession(IK6221CurrentSourcePort port, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(port); ArgumentNullException.ThrowIfNull(notifications);
        if (port.Descriptor.Model != "K6221" || port.Descriptor.StopPolicy != DeviceStopPolicy.OutputOff || !port.Descriptor.Parameters.SequenceEqual(Keithley6221Parameters.All))
            throw new ArgumentException("K6221 session requires its pure DC current-source profile.");
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
            var state = await port.GetSnapshotAsync(token); if (!state.Connected) state = await port.ConnectAsync(token);
            if (!state.Connected) throw new InvalidOperationException("K6221 connection not confirmed.");
            faulted = false; return await PublishAsync(state, token);
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
            await port.SafeStopAsync(token); var stopped = await port.GetSnapshotAsync(token);
            if (!stopped.Connected || stopped.OutputEnabled) throw new InvalidOperationException("K6221 output-off not confirmed before disconnect.");
            await port.DisconnectAsync(token); var after = await port.GetSnapshotAsync(token);
            if (after.Connected || after.OutputEnabled) throw new InvalidOperationException("K6221 disconnect not confirmed.");
            faulted = false; await PublishAsync(after, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => (await ReadObservationAsync(parameter, token, owner)).Value;
    public async Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
    {
        var descriptor = Descriptor.GetParameter(parameter); if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException("Parameter is write-only.");
        await operations.EnterAsync(token, owner);
        try
        {
            var state = await port.GetSnapshotAsync(token); if (!state.Connected) throw new InvalidOperationException("K6221 is disconnected.");
            var values = state.Settings!.CaptureParameters();
            var value = parameter == Keithley6221Parameters.Output.Id ? new Quantity(state.OutputEnabled ? 1 : 0, PhysicalUnit.Dimensionless)
                : values.TryGetValue(parameter, out var found) ? found : throw new InvalidOperationException("The requested register is unobserved; manual span is absent in autorange and old source delay may be unknown.");
            descriptor.Validate(value); await PublishAsync(state, token); return new(value, state.Timestamp);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Keithley6221Parameters.ValidateWrite(parameter, value); await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token); if (!before.Connected) throw new InvalidOperationException("K6221 is disconnected.");
            if (parameter == Keithley6221Parameters.Output.Id)
            {
                if (value.Value == 1 && faulted) throw new InvalidOperationException("K6221 safety recovery is required before enabling output.");
                await port.SetOutputAsync(value.Value == 1, token); var after = await port.GetSnapshotAsync(token);
                if (!after.Connected || after.OutputEnabled != (value.Value == 1) || after.Settings != before.Settings) throw new InvalidOperationException("K6221 output/configuration did not confirm.");
                await PublishAsync(after, token);
            }
            else
            {
                var requested = before.Settings!.WithParameter(parameter, value);
                if (requested != before.Settings) await port.ConfigureAsync(requested, token);
                var after = await port.GetSnapshotAsync(token);
                if (!after.Connected || before.OutputEnabled != after.OutputEnabled || after.Settings != requested) throw new InvalidOperationException("K6221 settings/output did not confirm.");
                await PublishAsync(after, token);
            }
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task ConfigureAsync(Keithley6221Settings settings, RunId owner, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings); await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token); if (!before.Connected || before.OutputEnabled) throw new InvalidOperationException("K6221 preparation requires connected output-off.");
            settings = new(settings.Current, settings.Compliance, settings.ManualRange, settings.SourceDelay ?? before.Settings!.SourceDelay);
            await port.ConfigureAsync(settings, token); var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || after.OutputEnabled || after.Settings != settings) throw new InvalidOperationException("K6221 prepared settings/output-off did not confirm.");
            await PublishAsync(after, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task<K6221CurrentSourceSnapshot> CaptureSourceStateAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var state = await port.GetSnapshotAsync(token); if (!state.Connected) throw new InvalidOperationException("K6221 connected state is required."); return state;
        }
        finally { operations.Exit(); }
    }
    internal async Task EnableOutputAsync(RunId owner, CancellationToken token, K6221CurrentSourceSnapshot? expected = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || faulted || expected is not null && (before.OutputEnabled || before.Settings != expected.Settings)) throw new InvalidOperationException("K6221 resume/output enable state differs from the confirmed settings.");
            await port.SetOutputAsync(true, token); var after = await port.GetSnapshotAsync(token);
            if (!after.Connected || !after.OutputEnabled || after.Settings != before.Settings) throw new InvalidOperationException("K6221 enabled output/configuration did not confirm.");
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
            await port.SafeStopAsync(token); var state = await port.GetSnapshotAsync(token);
            if (!state.Connected || state.OutputEnabled) throw new InvalidOperationException("K6221 output-off is unknown; ownership must be retained.");
            faulted = false; var status = await PublishAsync(state, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, $"{Descriptor.DisplayName}: output-off confirmed.", Descriptor.Id), token); return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    private async Task<DeviceStatus> PublishAsync(K6221CurrentSourceSnapshot state, CancellationToken token)
    {
        var lifecycle = !state.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted : state.OutputEnabled ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var values = state.Connected ? new Dictionary<ParameterId, Quantity>(state.Settings!.CaptureParameters()) : [];
        if (state.Connected) values.Add(Keithley6221Parameters.Output.Id, new(state.OutputEnabled ? 1 : 0, PhysicalUnit.Dimensionless));
        var status = new DeviceStatus(Descriptor, lifecycle, state.Connected, state.Connected && !state.OutputEnabled, state.Timestamp, values);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, $"{Descriptor.DisplayName}: {lifecycle}", Descriptor.Id, Device: status), token); return status;
    }
}

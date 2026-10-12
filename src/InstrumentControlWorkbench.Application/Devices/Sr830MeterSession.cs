using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class Sr830MeterSession : IDeviceSession
{
    private readonly ISr830MeterPort port;
    private readonly InstrumentOperationGate operations;
    private readonly NotificationHub notifications;
    private bool faulted;
    public Sr830MeterSession(ISr830MeterPort port, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(port); ArgumentNullException.ThrowIfNull(notifications);
        if (port.Descriptor.Model != "SR830" || port.Descriptor.StopPolicy != DeviceStopPolicy.AbortAcquisition || !port.Descriptor.Parameters.SequenceEqual(Sr830Parameters.All)) throw new ArgumentException("SR830 session requires its passive input profile.");
        this.port = port; this.notifications = notifications; operations = new(port.Descriptor.Id);
    }
    public DeviceDescriptor Descriptor => port.Descriptor;
    public Task ClaimRunAsync(RunId owner, CancellationToken token) => operations.ClaimAsync(owner, token);
    public Task ReleaseRunAsync(RunId owner, CancellationToken token) => operations.ReleaseAsync(owner, token);
    public async Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try { var state = await port.GetSnapshotAsync(token); if (!state.Connected) state = await port.ConnectAsync(token); RequireIdle(state); faulted = false; return await PublishAsync(state, token); }
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
            if ((await port.GetSnapshotAsync(token)).Acquiring) throw new InvalidOperationException("Stop acquisition before disconnecting the lock-in input.");
            await port.DisconnectAsync(token); var state = await port.GetSnapshotAsync(token);
            if (state.Connected) throw new InvalidOperationException("Lock-in input did not confirm disconnection.");
            await PublishAsync(state, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null) => (await ReadObservationAsync(parameter, token, owner)).Value;
    public async Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
    {
        var descriptor = Descriptor.GetParameter(parameter); if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException("Parameter is not readable.");
        if (parameter == Sr830Parameters.X.Id || parameter == Sr830Parameters.Y.Id || parameter == Sr830Parameters.R.Id || parameter == Sr830Parameters.Theta.Id)
        {
            var observed = await ReadFrameAsync(null, token, owner); var frame = observed.Frame;
            return new(parameter == Sr830Parameters.X.Id ? frame.X : parameter == Sr830Parameters.Y.Id ? frame.Y : parameter == Sr830Parameters.R.Id ? frame.R : frame.Theta, frame.CapturedAt);
        }
        await operations.EnterAsync(token, owner);
        try { var state = await port.GetSnapshotAsync(token); RequireIdle(state); return new(state.Settings!.CaptureParameters()[parameter], state.Timestamp); }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task<Sr830FrameObservation> ReadFrameAsync(Sr830InputMode? requiredInput, CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token); RequireIdle(before);
            if (requiredInput is { } input && before.Settings!.Input != input) throw new InvalidOperationException("Observed lock-in input differs from the frozen acquisition mode.");
            var frame = await port.ReadFrameAsync(token); token.ThrowIfCancellationRequested();
            var after = await port.GetSnapshotAsync(token); RequireIdle(after);
            if (after.Settings != before.Settings || frame.CapturedAt < before.Timestamp || frame.CapturedAt > after.Timestamp) throw new InvalidOperationException("Lock-in input settings or frame timestamp changed outside the acquisition boundary; discard the frame.");
            await PublishAsync(after, token); return new(frame, before.Settings!);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Sr830Parameters.ValidateWrite(parameter, value); await operations.EnterAsync(token, owner);
        try
        {
            var before = await port.GetSnapshotAsync(token); RequireIdle(before); var requested = before.Settings!.WithParameter(parameter, value);
            if (requested != before.Settings) await port.ConfigureAsync(requested, token);
            var after = await port.GetSnapshotAsync(token); RequireIdle(after);
            if (after.Settings != requested) throw new InvalidOperationException("Lock-in input settings did not confirm.");
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
            await port.AbortAcquisitionAsync(token); var state = await port.GetSnapshotAsync(token);
            if (state.Acquiring || !state.Connected && Descriptor.DriverKind == DriverKind.Hardware) throw new InvalidOperationException("Lock-in acquisition idle was not confirmed; retain ownership.");
            faulted = false; var status = await PublishAsync(state, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged, $"{Descriptor.DisplayName}: passive input acquisition idle confirmed; oscillator output is outside this profile.", Descriptor.Id), token); return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task<Sr830InputSettings> CaptureResumeSettingsAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try { var state = await port.GetSnapshotAsync(token); RequireIdle(state); return state.Settings!; }
        finally { operations.Exit(); }
    }
    private static void RequireIdle(Sr830MeterSnapshot state) { if (!state.Connected || state.Acquiring) throw new InvalidOperationException("Lock-in input must be connected and idle."); }
    private async Task<DeviceStatus> PublishAsync(Sr830MeterSnapshot snapshot, CancellationToken token)
    {
        var lifecycle = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted : snapshot.Acquiring ? InstrumentLifecycleState.Running : InstrumentLifecycleState.Idle;
        var status = new DeviceStatus(Descriptor, lifecycle, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.Acquiring,
            snapshot.Timestamp, snapshot.Connected ? snapshot.Settings!.CaptureParameters() : new Dictionary<ParameterId, Quantity>());
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, $"{Descriptor.DisplayName}: {lifecycle}", Descriptor.Id, Device: status), token); return status;
    }
}

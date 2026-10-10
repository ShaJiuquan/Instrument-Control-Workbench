using System.Globalization;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

// Run-owned routing lives here, not in a driver. Resource scopes park excitation
// sources before invoking relay writes; manual registry operations cannot route.
public sealed class MatrixSwitchSession : IDeviceSession
{
    private readonly IMatrixSwitchPort port;
    private readonly NotificationHub notifications;
    private readonly InstrumentOperationGate operations;
    private HashSet<MatrixCrossPoint>? baseline;
    private HashSet<MatrixCrossPoint>? expected;
    private bool faulted;
    internal bool HasRoutingMutations { get; private set; }
    public MatrixSwitchSession(IMatrixSwitchPort port, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(port); ArgumentNullException.ThrowIfNull(notifications);
        this.port = port; this.notifications = notifications; operations = new(port.Definition.Device.Id);
    }
    public MatrixSwitchDefinition Definition => port.Definition;
    public DeviceDescriptor Descriptor => Definition.Device;
    public async Task ClaimRunAsync(RunId owner, CancellationToken token)
    {
        await operations.ClaimAsync(owner, token);
        // Claim reset performs no IO and cannot be cancelled halfway through admission.
        await operations.EnterAsync(CancellationToken.None, owner);
        try { baseline = null; expected = null; HasRoutingMutations = false; }
        finally { operations.Exit(); }
    }
    public Task ReleaseRunAsync(RunId owner, CancellationToken token) => operations.ReleaseAsync(owner, token);
    public async Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected) snapshot = await port.ConnectAsync(token);
            RequireKnown(snapshot, requireConnection: true);
            if (owner is not null && baseline is null) baseline = snapshot.Closed.ToHashSet();
            expected ??= snapshot.Closed.ToHashSet();
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
            var snapshot = await port.GetSnapshotAsync(token);
            RequireKnown(snapshot, requireConnection: false);
            if (HasRoutingMutations && snapshot.Closed.Except(baseline!).Any())
                throw new InvalidOperationException("Confirm run-added connections are open before disconnecting.");
            await port.DisconnectAsync(token); await PublishAsync(await port.GetSnapshotAsync(token), token);
        }
        finally { operations.Exit(); }
    }
    public Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => throw new NotSupportedException("Matrix connections use typed crosspoints, not numeric parameters.");
    public Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
        => throw new NotSupportedException("Matrix connections use typed crosspoints, not numeric parameters.");

    internal async Task ReconcileAsync(IReadOnlyList<MatrixCrossPoint> desired, RunId owner, CancellationToken token)
    {
        Definition.Validate(desired);
        await operations.EnterAsync(token, owner);
        try
        {
            if (baseline is null) throw new InvalidOperationException("A known initial matrix snapshot is required before routing.");
            var snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
            var toOpen = snapshot.Closed.Except(desired).ToArray();
            if (toOpen.Length != 0)
            {
                HasRoutingMutations = true; await port.OpenAsync(toOpen, token);
                snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
                if (snapshot.Closed.Except(desired).Any()) throw new InvalidOperationException("Matrix open readback differs; no new connections were closed.");
            }
            var toClose = desired.Except(snapshot.Closed).ToArray();
            if (toClose.Length != 0) { HasRoutingMutations = true; await port.CloseAsync(toClose, token); }
            snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
            if (!desired.ToHashSet().SetEquals(snapshot.Closed)) throw new InvalidOperationException("Matrix route readback differs from the requested connection set.");
            expected = desired.ToHashSet(); faulted = false;
            await PublishAsync(snapshot, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.MatrixRoutingChanged,
                $"{Descriptor.DisplayName}: {snapshot.Closed.Count} connections verified; excitation remains off.", Descriptor.Id), token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    internal async Task ConfirmHeldRouteAsync(RunId owner, CancellationToken token, IReadOnlyList<MatrixCrossPoint>? desired = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
            if (expected is null || !expected.SetEquals(snapshot.Closed) || desired is not null && !expected.SetEquals(desired)) throw new InvalidOperationException("Matrix route changed; excitation must remain off.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    public Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null)
        => StopCoreAsync(token, owner, allowRelayChanges: false);

    internal Task<DeviceStatus> StopAfterSourcesAsync(CancellationToken token, RunId owner)
        => StopCoreAsync(token, owner, allowRelayChanges: true);

    private async Task<DeviceStatus> StopCoreAsync(CancellationToken token, RunId? owner, bool allowRelayChanges)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: false);
            if (HasRoutingMutations)
            {
                var added = snapshot.Closed.Except(baseline!).ToArray();
                if (added.Length != 0)
                {
                    if (!allowRelayChanges) throw new InvalidOperationException("Matrix cleanup requires a resource scope with confirmed excitation-off.");
                    await port.OpenAsync(added, token);
                }
                snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: false);
                if (snapshot.Closed.Except(baseline!).Any()) throw new InvalidOperationException("Matrix cleanup left run-added connections closed.");
            }
            // Deliberately do not reclose pre-existing connections opened by the run.
            expected = snapshot.Closed.ToHashSet(); faulted = false;
            var status = await PublishAsync(snapshot, token);
            await notifications.PublishAsync(new(WorkbenchNotificationKind.SafetyStateChanged,
                $"{Descriptor.DisplayName}: run-added connections confirmed open; existing opened routes were not reclosed.", Descriptor.Id), token);
            return status;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    internal async Task<InstrumentParameterSnapshot> CaptureParametersAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
            return new(Descriptor.Id, Descriptor.Model, snapshot.Timestamp, [], new Dictionary<string, string>
            {
                ["display_name"] = Descriptor.DisplayName,
                ["driver_kind"] = Descriptor.DriverKind.ToString(),
                ["stop_policy"] = Descriptor.StopPolicy.ToString(),
                ["cards"] = string.Join(";", Definition.Cards.Select(c => FormattableString.Invariant($"{c.Slot}:{c.Rows}:{c.Columns}"))),
                ["guarded_sources"] = string.Join(";", Definition.GuardedSources.Select(s => s.Value)),
                ["initial_closed"] = string.Join(";", (baseline ?? snapshot.Closed.ToHashSet()).OrderBy(p => p.Slot).ThenBy(p => p.Row).ThenBy(p => p.Column).Select(p =>
                    string.Create(CultureInfo.InvariantCulture, $"{p.Slot}:{p.Row}:{p.Column}"))),
            });
        }
        finally { operations.Exit(); }
    }
    private void RequireKnown(MatrixSwitchSnapshot snapshot, bool requireConnection)
    {
        if (snapshot.Definition != Definition || !snapshot.RouteKnown ||
            !snapshot.Connected && (requireConnection || Descriptor.DriverKind == DriverKind.Hardware))
            throw new InvalidOperationException($"{Descriptor.Id}: matrix routing is unknown or transport is disconnected.");
    }
    private async Task<DeviceStatus> PublishAsync(MatrixSwitchSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.Definition != Definition) throw new InvalidOperationException("Matrix snapshot belongs to a different definition.");
        var state = !snapshot.Connected ? InstrumentLifecycleState.Disconnected : faulted ? InstrumentLifecycleState.Faulted : InstrumentLifecycleState.Idle;
        var safe = snapshot.RouteKnown && (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) &&
            (!HasRoutingMutations || !snapshot.Closed.Except(baseline!).Any());
        var status = new DeviceStatus(Descriptor, state, snapshot.Connected, safe, snapshot.Timestamp, new Dictionary<ParameterId, Quantity>());
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DeviceStateChanged, $"{Descriptor.DisplayName}: {state}", Descriptor.Id, Device: status), token);
        return status;
    }
}

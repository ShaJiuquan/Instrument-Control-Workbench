using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class MatrixSwitchSession
{
    private IReadOnlyList<MatrixCrossPoint> RequireAcConnections(InstrumentId generator, IReadOnlyList<MatrixCrossPoint> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var capability = Definition.AcIsolation ?? throw new NotSupportedException("Matrix has no registered AC isolation capability.");
        if (capability.Generator != generator || connections.Count != capability.Connections.Count ||
            connections.Any(p => p is null) || !capability.Connections.ToHashSet().SetEquals(connections))
            throw new ArgumentException("AC isolation must use its registered generator and exact complete disconnect set.", nameof(connections));
        return capability.Connections;
    }

    internal async Task<MatrixSwitchSnapshot> SetAcConnectionsAsync(InstrumentId generator, IReadOnlyList<MatrixCrossPoint> connections, bool connect, RunId owner, CancellationToken token)
    {
        var selected = RequireAcConnections(generator, connections);
        await operations.EnterAsync(token, owner);
        try
        {
            MatrixSwitchSnapshot snapshot;
            if (connect)
            {
                snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
                if (baseline is null || expected is null || !expected.SetEquals(snapshot.Closed) || snapshot.Closed.Intersect(selected).Any())
                    throw new InvalidOperationException("AC connection requires freshly confirmed expected routing with every selected path open.");
                var desired = snapshot.Closed.Concat(selected).ToHashSet();
                if (desired.Count > 4096) throw new InvalidOperationException("AC connection would exceed the matrix snapshot crosspoint budget.");
                HasRoutingMutations = true; await port.CloseAsync(selected, token);
                snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
                if (!desired.SetEquals(snapshot.Closed)) throw new InvalidOperationException("AC connection did not confirm or unrelated routes changed.");
            }
            else
            {
                // Opening must not be suppressed by stale baseline, route drift or unknown routing.
                HasRoutingMutations = true; await port.OpenAsync(selected, token);
                snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
                if (snapshot.Closed.Intersect(selected).Any()) throw new InvalidOperationException("AC isolation paths did not confirm open; retain ownership.");
                // An emergency open may be the first successful known observation.
                baseline ??= snapshot.Closed.ToHashSet();
            }
            expected = snapshot.Closed.ToHashSet(); faulted = false;
            await PublishAsync(snapshot, token);
            return snapshot;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    internal async Task<MatrixSwitchSnapshot> ConfirmAcConnectionsAsync(InstrumentId generator, IReadOnlyList<MatrixCrossPoint> connections, bool connected, RunId owner, CancellationToken token)
    {
        var selected = RequireAcConnections(generator, connections);
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await port.GetSnapshotAsync(token); RequireKnown(snapshot, requireConnection: true);
            if (expected is null || !expected.SetEquals(snapshot.Closed) || selected.Any(point => snapshot.Closed.Contains(point) != connected))
                throw new InvalidOperationException("AC isolation routing changed or selected paths are not in the required state.");
            await PublishAsync(snapshot, token);
            return snapshot;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
}

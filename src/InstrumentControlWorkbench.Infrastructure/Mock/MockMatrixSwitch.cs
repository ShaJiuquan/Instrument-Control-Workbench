using InstrumentControlWorkbench.Core.Devices;

namespace InstrumentControlWorkbench.Infrastructure.Mock;

// Relays latch across transport disconnects. No simulated electrical topology is implied.
public sealed class MockMatrixSwitch : IMatrixSwitchPort
{
    private readonly object gate = new();
    private readonly HashSet<MatrixCrossPoint> closed;
    private bool connected;
    public MockMatrixSwitch(MatrixSwitchDefinition definition, IEnumerable<MatrixCrossPoint>? initiallyClosed = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Device.DriverKind != DriverKind.Simulation) throw new ArgumentException("Mock matrix must be labeled Simulation.", nameof(definition));
        Definition = definition; closed = (initiallyClosed ?? []).ToHashSet(); definition.Validate(closed);
    }
    public MatrixSwitchDefinition Definition { get; }
    public Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); lock (gate) { connected = true; return Task.FromResult(Snapshot()); } }
    public Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); lock (gate) return Task.FromResult(Snapshot()); }
    public Task DisconnectAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); lock (gate) connected = false; return Task.CompletedTask; }
    public Task OpenAsync(IReadOnlyList<MatrixCrossPoint> crossPoints, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Definition.Validate(crossPoints); lock (gate) closed.ExceptWith(crossPoints); return Task.CompletedTask; }
    public Task CloseAsync(IReadOnlyList<MatrixCrossPoint> crossPoints, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Definition.Validate(crossPoints);
        lock (gate) { if (!connected) throw new InvalidOperationException("Matrix is disconnected."); closed.UnionWith(crossPoints); }
        return Task.CompletedTask;
    }
    private MatrixSwitchSnapshot Snapshot() => new(Definition, connected, true, closed.OrderBy(p => p.Slot).ThenBy(p => p.Row).ThenBy(p => p.Column), DateTimeOffset.UtcNow);
}

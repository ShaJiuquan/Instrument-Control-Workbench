using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Devices;

// Domain addresses are independent of SCPI channel-number formatting.
public sealed record MatrixCrossPoint
{
    public MatrixCrossPoint(int slot, int row, int column)
    {
        if (slot <= 0 || row <= 0 || column <= 0) throw new ArgumentOutOfRangeException(nameof(slot), "Slot, row and column must be positive.");
        Slot = slot; Row = row; Column = column;
    }
    public int Slot { get; }
    public int Row { get; }
    public int Column { get; }
    public override string ToString() => FormattableString.Invariant($"{Slot}:{Row}:{Column}");
}

public sealed record MatrixCardDefinition
{
    public MatrixCardDefinition(int slot, int rows, int columns)
    {
        if (slot <= 0 || rows <= 0 || columns <= 0) throw new ArgumentOutOfRangeException(nameof(slot));
        Slot = slot; Rows = rows; Columns = columns;
    }
    public int Slot { get; }
    public int Rows { get; }
    public int Columns { get; }
}

public sealed class MatrixSwitchDefinition
{
    public MatrixSwitchDefinition(DeviceDescriptor device, IEnumerable<MatrixCardDefinition> cards, IEnumerable<InstrumentId> guardedSources, MatrixAcIsolationCapability? acIsolation = null)
    {
        ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(cards); ArgumentNullException.ThrowIfNull(guardedSources);
        if (device.StopPolicy != DeviceStopPolicy.OpenRunAddedConnections || device.Parameters.Count != 0)
            throw new ArgumentException("Matrix devices need the run-added-connection stop policy and use crosspoints rather than numeric parameters.", nameof(device));
        var items = cards.ToArray(); var sources = guardedSources.ToArray();
        if (items.Length is < 1 or > 64 || items.Any(c => c is null) || items.Select(c => c.Slot).Distinct().Count() != items.Length)
            throw new ArgumentException("Declare one to 64 cards with unique slots.", nameof(cards));
        if (sources.Length > 64 || sources.Length == 0 && acIsolation is null || sources.Any(s => string.IsNullOrWhiteSpace(s.Value) || s == device.Id) || sources.Distinct().Count() != sources.Length)
            throw new ArgumentException("Declare up to 64 distinct excitation sources, excluding the matrix itself; at least one is required without registered AC isolation.", nameof(guardedSources));
        if (acIsolation?.Generator == device.Id) throw new ArgumentException("AC isolation requires a generator separate from the matrix.", nameof(acIsolation));
        Device = device; Cards = Array.AsReadOnly(items); GuardedSources = Array.AsReadOnly(sources); AcIsolation = acIsolation;
        if (acIsolation is not null) Validate(acIsolation.Connections);
    }
    public DeviceDescriptor Device { get; }
    public IReadOnlyList<MatrixCardDefinition> Cards { get; }
    public IReadOnlyList<InstrumentId> GuardedSources { get; }
    public MatrixAcIsolationCapability? AcIsolation { get; }
    public void Validate(IEnumerable<MatrixCrossPoint> crossPoints)
    {
        ArgumentNullException.ThrowIfNull(crossPoints);
        foreach (var point in crossPoints)
        {
            ArgumentNullException.ThrowIfNull(point);
            var card = Cards.SingleOrDefault(c => c.Slot == point.Slot);
            if (card is null || point.Row > card.Rows || point.Column > card.Columns)
                throw new ArgumentOutOfRangeException(nameof(crossPoints), $"{point} is outside the configured matrix cards.");
        }
    }
}

public sealed record MatrixSwitchSnapshot
{
    public MatrixSwitchSnapshot(MatrixSwitchDefinition definition, bool connected, bool routeKnown,
        IEnumerable<MatrixCrossPoint> closed, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(closed);
        var points = closed.ToArray();
        if (points.Length > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length)
            throw new ArgumentException("Matrix snapshot crosspoints must be unique and within the 4096-point software budget.", nameof(closed));
        definition.Validate(points);
        if (!routeKnown && points.Length != 0) throw new ArgumentException("Unknown routing must not publish a cached closed set.", nameof(closed));
        Definition = definition; Connected = connected; RouteKnown = routeKnown; Closed = Array.AsReadOnly(points); Timestamp = timestamp;
    }
    public MatrixSwitchDefinition Definition { get; }
    public bool Connected { get; }
    public bool RouteKnown { get; }
    public IReadOnlyList<MatrixCrossPoint> Closed { get; }
    public DateTimeOffset Timestamp { get; }
}

public interface IMatrixSwitchPort
{
    MatrixSwitchDefinition Definition { get; }
    Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token);
    Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
    Task OpenAsync(IReadOnlyList<MatrixCrossPoint> crossPoints, CancellationToken token);
    Task CloseAsync(IReadOnlyList<MatrixCrossPoint> crossPoints, CancellationToken token);
}

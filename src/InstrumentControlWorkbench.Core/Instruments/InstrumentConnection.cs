namespace InstrumentControlWorkbench.Core.Instruments;

public enum InstrumentConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

public sealed record InstrumentConnection(
    string Id,
    string DisplayName,
    string Role,
    string Address,
    InstrumentConnectionState State,
    string Detail,
    DateTimeOffset LastUpdated)
{
    public bool IsStale(TimeSpan ttl, DateTimeOffset now) => now - LastUpdated > ttl;
}

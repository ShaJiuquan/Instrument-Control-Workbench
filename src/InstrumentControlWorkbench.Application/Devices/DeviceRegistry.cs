using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed class DeviceRegistry
{
    private readonly IReadOnlyDictionary<InstrumentId, IDeviceSession> sessions;
    private readonly InstrumentLeaseService leases;

    public DeviceRegistry(IEnumerable<IDeviceSession> sessions, InstrumentLeaseService leases)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var items = sessions.ToArray();
        if (items.Any(d => d is null) || items.Select(d => d.Descriptor.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("Devices must be non-null with unique instance IDs.", nameof(sessions));
        this.sessions = items.ToDictionary(d => d.Descriptor.Id);
        this.leases = leases;
        Devices = Array.AsReadOnly(items.Select(d => d.Descriptor).ToArray());
    }

    public IReadOnlyList<DeviceDescriptor> Devices { get; }
    public bool SupportsTemperatureConfiguration(InstrumentId id) => Get(id) is AxisControllerSession { SupportsTemperatureConfiguration: true };
    public Task ConfigureTemperatureAsync(InstrumentId id, TemperatureControlAction action, Quantity? rate, CancellationToken token)
        => GetManual(id) is AxisControllerSession axis ? axis.ConfigureTemperatureAsync(action, rate, token)
            : throw new NotSupportedException($"{id} has no temperature-control profile.");
    public AxisControllerDefinition GetAxisDefinition(InstrumentId id) => Get(id) is AxisControllerSession axis ? axis.Definition
        : throw new NotSupportedException($"{id} has no axis-controller execution profile.");
    public MatrixSwitchDefinition GetMatrixDefinition(InstrumentId id) => Get(id) is MatrixSwitchSession matrix ? matrix.Definition
        : throw new NotSupportedException($"{id} has no matrix-routing execution profile.");
    public Task<DeviceStatus> ConnectAsync(InstrumentId id, CancellationToken token) => GetManual(id).ConnectDeviceAsync(token);
    public Task DisconnectAsync(InstrumentId id, CancellationToken token) => GetManual(id).DisconnectDeviceAsync(token);
    public Task<DeviceStatus> GetStatusAsync(InstrumentId id, CancellationToken token) => GetManual(id).GetDeviceStatusAsync(token);
    public Task<Quantity> ReadAsync(InstrumentId id, ParameterId parameter, CancellationToken token) => GetManual(id).ReadParameterAsync(parameter, token);
    public Task WriteAsync(InstrumentId id, ParameterId parameter, Quantity value, CancellationToken token) => GetManual(id).WriteParameterAsync(parameter, value, token);

    public async Task<DeviceRunResources> AcquireRunAsync(IEnumerable<InstrumentId> ids, RunId owner, CancellationToken token,
        TimeSpan? safetyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(owner);
        token.ThrowIfCancellationRequested();
        var requested = ids.Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).Select(Get).ToArray();
        if (requested.Length == 0) throw new ArgumentException("A run requires at least one device.", nameof(ids));
        var timeout = safetyTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(safetyTimeout), "Safety timeout must be positive and fit the runtime timer range.");
        leases.AcquireRunExclusive(requested.Select(d => d.Descriptor.Id), owner, DateTimeOffset.UtcNow);
        var claimed = new List<IDeviceSession>();
        try
        {
            foreach (var session in requested)
            {
                await session.ClaimRunAsync(owner, token);
                claimed.Add(session);
            }
            return new(requested, owner, leases, timeout);
        }
        catch
        {
            // Claims never touch hardware. Roll back just these claims/leases;
            // stopping on admission failure could interfere with another owner.
            foreach (var session in claimed.AsEnumerable().Reverse()) await session.ReleaseRunAsync(owner, CancellationToken.None);
            foreach (var session in requested) leases.Release(session.Descriptor.Id, owner.ToString());
            throw;
        }
    }

    private IDeviceSession Get(InstrumentId id) => sessions.TryGetValue(id, out var session) ? session
        : throw new KeyNotFoundException($"No device instance is registered as {id}.");

    private IDeviceSession GetManual(InstrumentId id)
    {
        var session = Get(id);
        if (leases.IsLeased(id, DateTimeOffset.UtcNow)) throw new InvalidOperationException($"{id} is reserved; manual operations are unavailable.");
        return session;
    }
}

using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Devices;

public enum ConfiguredDeviceCategory { SourceMeter, Source, Meter, LockIn, Matrix, Temperature, Field, Rotation }

public abstract record DeviceConnection;
public sealed record SimulationDeviceConnection : DeviceConnection;
public sealed record TcpDeviceConnection : DeviceConnection
{
    public TcpDeviceConnection(string host, int port, string terminator, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host); ArgumentNullException.ThrowIfNull(terminator);
        if (host.Length > 1024 || host.Any(char.IsControl) || host.Any(char.IsWhiteSpace) || port is < 1 or > 65535 ||
            terminator.Length > 16 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(host), "Invalid TCP host/port/terminator or runtime timeout.");
        Host = host; Port = port; Terminator = terminator; Timeout = timeout;
    }
    public string Host { get; }
    public int Port { get; }
    public string Terminator { get; }
    public TimeSpan Timeout { get; }
}
public sealed record VisaDeviceConnection : DeviceConnection
{
    public VisaDeviceConnection(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (resource.Length > 1024 || resource.Any(char.IsControl)) throw new ArgumentOutOfRangeException(nameof(resource));
        Resource = resource;
    }
    public string Resource { get; }
}
public sealed record SerialDeviceConnection : DeviceConnection
{
    public SerialDeviceConnection(string port, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        if (port.Length > 1024 || port.Any(char.IsControl) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(port), "Invalid serial port or runtime timeout.");
        Port = port; Timeout = timeout;
    }
    public string Port { get; }
    public TimeSpan Timeout { get; }
}
public sealed class ConfiguredMatrix
{
    public ConfiguredMatrix(IEnumerable<MatrixCardDefinition> cards, IEnumerable<InstrumentId> guardedSources, MatrixAcIsolationCapability? acIsolation = null)
    {
        ArgumentNullException.ThrowIfNull(cards); ArgumentNullException.ThrowIfNull(guardedSources);
        var c = cards.ToArray(); var s = guardedSources.ToArray();
        if (c.Length is < 1 or > 64 || c.Any(p => p is null) || c.Select(p => p.Slot).Distinct().Count() != c.Length ||
            s.Length > 64 || s.Length == 0 && acIsolation is null || s.Any(p => string.IsNullOrWhiteSpace(p.Value)) || s.Distinct().Count() != s.Length)
            throw new ArgumentException("Matrix configuration requires one to 64 distinct cards and up to 64 guarded source IDs; an empty guard set requires registered AC isolation.");
        if (acIsolation is not null)
            foreach (var point in acIsolation.Connections)
            {
                var card = c.SingleOrDefault(card => card.Slot == point.Slot);
                if (card is null || point.Row > card.Rows || point.Column > card.Columns)
                    throw new ArgumentException("AC isolation connection is outside the declared matrix cards.");
            }
        Cards = Array.AsReadOnly(c); GuardedSources = Array.AsReadOnly(s); AcIsolation = acIsolation;
    }
    public IReadOnlyList<MatrixCardDefinition> Cards { get; }
    public IReadOnlyList<InstrumentId> GuardedSources { get; }
    public MatrixAcIsolationCapability? AcIsolation { get; }
}
public sealed record DeviceConfiguration
{
    public DeviceConfiguration(InstrumentId id, string model, string displayName, ConfiguredDeviceCategory category,
        DeviceConnection connection, bool enabled = true, ConfiguredMatrix? matrix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value); ArgumentException.ThrowIfNullOrWhiteSpace(model); ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(connection);
        if (id.Value.Length > 128 || id.Value != id.Value.Trim() || model.Length > 128 || model != model.Trim() || displayName.Length > 1024 || id.Value.Any(char.IsControl) ||
            !Enum.IsDefined(category) || matrix is not null && category != ConfiguredDeviceCategory.Matrix ||
            connection is not (SimulationDeviceConnection or TcpDeviceConnection or VisaDeviceConnection or SerialDeviceConnection))
            throw new ArgumentException("Invalid device configuration identity, category or matrix declaration.");
        Id = id; Model = model; DisplayName = displayName; Category = category; Connection = connection; Enabled = enabled; Matrix = matrix;
    }
    public InstrumentId Id { get; }
    public string Model { get; }
    public string DisplayName { get; }
    public ConfiguredDeviceCategory Category { get; }
    public DeviceConnection Connection { get; }
    public bool Enabled { get; }
    public ConfiguredMatrix? Matrix { get; }
    public DriverKind DriverKind => Connection is SimulationDeviceConnection ? DriverKind.Simulation : DriverKind.Hardware;
}
public sealed class DeviceConfigurationCatalog
{
    public DeviceConfigurationCatalog(InstrumentId? primarySource, IEnumerable<DeviceConfiguration> devices,
        IEnumerable<KeyValuePair<string, InstrumentId>> aliases)
    {
        ArgumentNullException.ThrowIfNull(devices); ArgumentNullException.ThrowIfNull(aliases);
        var d = devices.ToArray(); var a = aliases.ToArray();
        if (d.Length is < 1 or > 64 || d.Any(p => p is null) || d.Select(p => p.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != d.Length)
            throw new ArgumentException("Device settings require one to 64 unique instances.");
        if (primarySource is { } selected && !d.Any(p => p.Id == selected && p.Enabled && p.Category == ConfiguredDeviceCategory.SourceMeter))
            throw new ArgumentException("Select an enabled source-meter instance as the primary source.");
        var endpoints = d.Where(p => p.Enabled && p.DriverKind == DriverKind.Hardware).Select(p => p.Connection switch
        {
            TcpDeviceConnection tcp => "tcp:" + tcp.Host.ToLowerInvariant() + ":" + tcp.Port,
            VisaDeviceConnection visa => "visa:" + visa.Resource.ToUpperInvariant(),
            SerialDeviceConnection serial => "serial:" + serial.Port.ToUpperInvariant(),
            _ => throw new ArgumentException("Unknown hardware connection."),
        }).ToArray();
        if (endpoints.Distinct(StringComparer.Ordinal).Count() != endpoints.Length)
            throw new ArgumentException("Enabled hardware instances must not share an endpoint in one catalog.");
        if (a.Length > 64 || a.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Key != p.Key.Trim() || p.Key.Any(char.IsControl) || !d.Any(device => device.Id == p.Value)) ||
            a.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.Length)
            throw new ArgumentException("Device aliases must be distinct and reference existing instances.");
        if (d.Where(p => p.Matrix?.AcIsolation is not null).GroupBy(p => p.Matrix!.AcIsolation!.Generator).Any(g => g.Count() > 1))
            throw new ArgumentException("Each active AC generator must have exactly one registered isolation matrix in this profile.");
        foreach (var device in d)
            foreach (var source in device.Matrix?.GuardedSources ?? [])
                if (!d.Any(p => p.Id == source && (p.Category is ConfiguredDeviceCategory.SourceMeter or ConfiguredDeviceCategory.Source) && (!device.Enabled || p.Enabled)))
                    throw new ArgumentException($"{device.Id}: guarded source {source} is missing, disabled or not an excitation source.");
        foreach (var matrix in d.Where(device => device.Matrix?.AcIsolation is not null))
        {
            var isolation = matrix.Matrix!.AcIsolation!;
            if (isolation.Generator == matrix.Id || !d.Any(generator => generator.Id == isolation.Generator &&
                generator.Category == ConfiguredDeviceCategory.LockIn && generator.Model.Equals("SR830-AC-SIM", StringComparison.OrdinalIgnoreCase) &&
                generator.Connection is SimulationDeviceConnection && (!matrix.Enabled || generator.Enabled)))
                throw new ArgumentException($"{matrix.Id}: AC isolation requires a separate registered active SR830 simulation generator; passive SR830 cannot grant excitation capability.");
        }
        foreach (var pair in a)
        {
            ConfiguredDeviceCategory? required = pair.Key.ToLowerInvariant() switch { "temperature" => ConfiguredDeviceCategory.Temperature, "field" => ConfiguredDeviceCategory.Field, "angle" => ConfiguredDeviceCategory.Rotation, _ => null };
            if (required is { } category && d.Single(p => p.Id == pair.Value).Category != category)
                throw new ArgumentException($"Alias {pair.Key} requires a {category} controller.");
        }
        PrimarySource = primarySource; Devices = Array.AsReadOnly(d);
        Aliases = new ReadOnlyDictionary<string, InstrumentId>(a.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }
    public InstrumentId? PrimarySource { get; }
    public IReadOnlyList<DeviceConfiguration> Devices { get; }
    public IReadOnlyDictionary<string, InstrumentId> Aliases { get; }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Routing;

public enum SampleTopology { TwoWire, FourWire, HallBar, VanDerPauw }
public enum RoutingRole { SourcePlus, SourceMinus, SensePlus, SenseMinus }
public enum SampleRouteSelection
{
    TwoWireResistance, FourWireResistance, HallLongitudinal, HallTransverse,
    VdpAbCd, VdpBaCd, VdpAbDc, VdpBaDc, VdpBcDa, VdpCbDa, VdpBcAd, VdpCbAd,
}
public sealed record MatrixPort
{
    public MatrixPort(int slot, int row)
    {
        if (slot <= 0 || row <= 0) throw new ArgumentOutOfRangeException(nameof(slot));
        Slot = slot; Row = row;
    }
    public int Slot { get; }
    public int Row { get; }
}
public sealed record ChannelConnection
{
    public ChannelConnection(ConnectionKey key, int? slot, int? column, bool enabled = true, int? index = null, string label = "", int? displayRow = null, string raw = "", string notes = "")
    {
        ArgumentNullException.ThrowIfNull(key);
        if (slot is <= 0 || column is <= 0) throw new ArgumentOutOfRangeException(nameof(slot));
        if (index is < 0 || displayRow is <= 0) throw new ArgumentOutOfRangeException(nameof(index));
        ProfileText.Validate(label); ProfileText.Validate(raw); ProfileText.Validate(notes);
        Key = key; Slot = slot; Column = column; Enabled = enabled;
        Index = index; Label = label; DisplayRow = displayRow; Raw = raw; Notes = notes;
    }
    public ConnectionKey Key { get; }
    public int? Slot { get; }
    public int? Column { get; }
    public bool Enabled { get; }
    public int? Index { get; }
    public string Label { get; }
    // PPMS channel rows/raw addresses are presentation data; instrument wiring owns executable rows.
    public int? DisplayRow { get; }
    public string Raw { get; }
    public string Notes { get; }
}
public sealed record ChannelProfile
{
    public ChannelProfile(ChannelProfileId id, string name, IEnumerable<ChannelConnection> connections, string originalId = "", int? groupIndex = null, string alias = "", string notes = "")
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(connections);
        var items = connections.ToArray();
        if (name.Length > 1024 || items.Length is < 1 or > 64 || items.Any(c => c is null) ||
            items.Select(c => c.Key.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new ArgumentException("Channels need a bounded name and one to 64 distinct connections.", nameof(connections));
        ProfileText.Validate(originalId); ProfileText.Validate(alias); ProfileText.Validate(notes);
        if (groupIndex is < 0) throw new ArgumentOutOfRangeException(nameof(groupIndex));
        Id = id; Name = name; Connections = Array.AsReadOnly(items);
        OriginalId = originalId; GroupIndex = groupIndex; Alias = alias; Notes = notes;
    }
    public ChannelProfileId Id { get; }
    public string Name { get; }
    public IReadOnlyList<ChannelConnection> Connections { get; }
    public string OriginalId { get; }
    public int? GroupIndex { get; }
    public string Alias { get; }
    public string Notes { get; }
}
public sealed record InstrumentWiringProfile
{
    public InstrumentWiringProfile(WiringProfileId id, InstrumentId sourceMeter, MatrixPort sourcePlus, MatrixPort sourceMinus, MatrixPort sensePlus, MatrixPort senseMinus, string label = "")
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentException.ThrowIfNullOrWhiteSpace(sourceMeter.Value);
        ArgumentNullException.ThrowIfNull(sourcePlus); ArgumentNullException.ThrowIfNull(sourceMinus);
        ArgumentNullException.ThrowIfNull(sensePlus); ArgumentNullException.ThrowIfNull(senseMinus);
        ProfileText.Validate(label); Label = label; Id = id; SourceMeter = sourceMeter; SourcePlus = sourcePlus; SourceMinus = sourceMinus; SensePlus = sensePlus; SenseMinus = senseMinus;
    }
    public WiringProfileId Id { get; }
    public InstrumentId SourceMeter { get; }
    public string Label { get; }
    public MatrixPort SourcePlus { get; }
    public MatrixPort SourceMinus { get; }
    public MatrixPort SensePlus { get; }
    public MatrixPort SenseMinus { get; }
    public MatrixPort Port(RoutingRole role) => role switch
    {
        RoutingRole.SourcePlus => SourcePlus,
        RoutingRole.SourceMinus => SourceMinus,
        RoutingRole.SensePlus => SensePlus,
        RoutingRole.SenseMinus => SenseMinus,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
public sealed record SampleElectrode
{
    public SampleElectrode(ElectrodeLabel label, ChannelProfileId channel, ConnectionKey connection, bool enabled = true, string notes = "")
    {
        ArgumentNullException.ThrowIfNull(label); ArgumentNullException.ThrowIfNull(channel); ArgumentNullException.ThrowIfNull(connection);
        ProfileText.Validate(notes); Label = label; Channel = channel; Connection = connection; Enabled = enabled; Notes = notes;
    }
    public ElectrodeLabel Label { get; }
    public ChannelProfileId Channel { get; }
    public ConnectionKey Connection { get; }
    public bool Enabled { get; }
    public string Notes { get; }
}
public sealed record SampleProfile
{
    public SampleProfile(SampleId id, string name, SampleTopology topology, IEnumerable<SampleElectrode> electrodes, SampleGeometry? geometry = null, string originalId = "", string material = "", string substrate = "", string notes = "")
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(electrodes);
        if (!Enum.IsDefined(topology)) throw new ArgumentOutOfRangeException(nameof(topology));
        var items = electrodes.ToArray();
        if (name.Length > 1024 || items.Length is < 1 or > 64 || items.Any(e => e is null) ||
            items.Select(e => e.Label.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new ArgumentException("Samples need a bounded name and one to 64 distinct electrode labels.", nameof(electrodes));
        var labels = items.Select(e => e.Label.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (RequiredLabels(topology).Any(l => !labels.Contains(l))) throw new ArgumentException("Sample is missing electrodes required by its topology.", nameof(electrodes));
        ProfileText.Validate(originalId); ProfileText.Validate(material); ProfileText.Validate(substrate); ProfileText.Validate(notes);
        Id = id; Name = name; Topology = topology; Electrodes = Array.AsReadOnly(items); Geometry = geometry ?? SampleGeometry.Empty;
        OriginalId = originalId; Material = material; Substrate = substrate; Notes = notes;
    }
    public SampleId Id { get; }
    public string Name { get; }
    public SampleTopology Topology { get; }
    public IReadOnlyList<SampleElectrode> Electrodes { get; }
    public SampleGeometry Geometry { get; }
    public string OriginalId { get; }
    public string Material { get; }
    public string Substrate { get; }
    public string Notes { get; }
    public static IReadOnlyList<string> RequiredLabels(SampleTopology topology) => topology switch
    {
        SampleTopology.TwoWire => Array.AsReadOnly(new[] { "A", "B" }),
        SampleTopology.FourWire => Array.AsReadOnly(new[] { "I+", "I-", "V+", "V-" }),
        SampleTopology.HallBar => Array.AsReadOnly(new[] { "I+", "I-", "Vxx+", "Vxx-", "Vxy+", "Vxy-" }),
        SampleTopology.VanDerPauw => Array.AsReadOnly(new[] { "A", "B", "C", "D" }),
        _ => throw new ArgumentOutOfRangeException(nameof(topology)),
    };
}

internal static class ProfileText
{
    public static void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 8192) throw new ArgumentException("Profile text exceeds 8192 characters.");
    }
}

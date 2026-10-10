using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Routing;

public sealed record SampleRoutingBinding
{
    public SampleRoutingBinding(SampleId sample, WiringProfileId wiring, SampleRouteSelection selection)
    {
        ArgumentNullException.ThrowIfNull(sample); ArgumentNullException.ThrowIfNull(wiring);
        if (!Enum.IsDefined(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        Sample = sample; Wiring = wiring; Selection = selection;
    }
    public SampleId Sample { get; }
    public WiringProfileId Wiring { get; }
    public SampleRouteSelection Selection { get; }
}
public sealed record ResolvedElectrodeRoute
{
    public ResolvedElectrodeRoute(RoutingRole role, ElectrodeLabel electrode, ChannelProfileId channel, ConnectionKey connection, MatrixCrossPoint point)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(electrode); ArgumentNullException.ThrowIfNull(channel); ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(point);
        Role = role; Electrode = electrode; Channel = channel; Connection = connection; Point = point;
    }
    public RoutingRole Role { get; }
    public ElectrodeLabel Electrode { get; }
    public ChannelProfileId Channel { get; }
    public ConnectionKey Connection { get; }
    public MatrixCrossPoint Point { get; }
}
// Immutable per-row provenance, also validated after archive decoding.
public sealed record ResolvedSampleRoute
{
    public ResolvedSampleRoute(SampleRoutingBinding binding, string sampleName, SampleTopology topology, InstrumentId matrix,
        InstrumentId sourceMeter, IEnumerable<ResolvedElectrodeRoute> roles)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(matrix.Value); ArgumentException.ThrowIfNullOrWhiteSpace(sourceMeter.Value); ArgumentNullException.ThrowIfNull(roles);
        if (!Enum.IsDefined(topology) || sampleName.Length > 1024) throw new ArgumentOutOfRangeException(nameof(topology));
        var items = roles.OrderBy(r => r?.Role).ToArray();
        if (items.Length != 4 || items.Any(r => r is null) || items.Select(r => r.Role).Distinct().Count() != 4)
            throw new ArgumentException("A sample route needs all four distinct roles.", nameof(roles));
        var expectedLabels = Labels(topology, binding.Selection);
        for (var i = 0; i < items.Length; i++)
            if (!StringComparer.OrdinalIgnoreCase.Equals(items[i].Electrode.Value, expectedLabels[i]))
                throw new ArgumentException("Role assignment does not match the selected sample measurement.", nameof(roles));
        var ports = items.Select(r => new MatrixPort(r.Point.Slot, r.Point.Row)).ToArray();
        if (new[] { ports[0], ports[2] }.Intersect(new[] { ports[1], ports[3] }).Any())
            throw new ArgumentException("Opposite measurement polarities cannot share an instrument matrix port.", nameof(roles));
        var terminals = items.Select(r => (r.Point.Slot, r.Point.Column)).ToArray();
        if (topology != SampleTopology.TwoWire && (ports.Distinct().Count() != 4 || terminals.Distinct().Count() != 4))
            throw new ArgumentException("Four-wire measurement requires four independent instrument ports and sample terminals.", nameof(roles));
        if (topology == SampleTopology.TwoWire && (terminals[0] != terminals[2] || terminals[1] != terminals[3] || terminals[0] == terminals[1]))
            throw new ArgumentException("Two-wire measurement must share each polarity's electrode, with distinct positive and negative terminals.", nameof(roles));
        Binding = binding; SampleName = sampleName; Topology = topology; Matrix = matrix; SourceMeter = sourceMeter;
        Roles = Array.AsReadOnly(items); Closed = Array.AsReadOnly(items.Select(r => r.Point).Distinct().ToArray());
    }
    public SampleRoutingBinding Binding { get; }
    public string SampleName { get; }
    public SampleTopology Topology { get; }
    public InstrumentId Matrix { get; }
    public InstrumentId SourceMeter { get; }
    public IReadOnlyList<ResolvedElectrodeRoute> Roles { get; }
    public IReadOnlyList<MatrixCrossPoint> Closed { get; }
    public SensingMode SensingMode => Topology == SampleTopology.TwoWire ? SensingMode.TwoWire : SensingMode.FourWire;
    internal static string[] Labels(SampleTopology topology, SampleRouteSelection selection) => (topology, selection) switch
    {
        (SampleTopology.TwoWire, SampleRouteSelection.TwoWireResistance) => ["A", "B", "A", "B"],
        (SampleTopology.FourWire, SampleRouteSelection.FourWireResistance) => ["I+", "I-", "V+", "V-"],
        (SampleTopology.HallBar, SampleRouteSelection.HallLongitudinal) => ["I+", "I-", "Vxx+", "Vxx-"],
        (SampleTopology.HallBar, SampleRouteSelection.HallTransverse) => ["I+", "I-", "Vxy+", "Vxy-"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpAbCd) => ["A", "B", "C", "D"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpBaCd) => ["B", "A", "C", "D"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpAbDc) => ["A", "B", "D", "C"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpBaDc) => ["B", "A", "D", "C"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpBcDa) => ["B", "C", "D", "A"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpCbDa) => ["C", "B", "D", "A"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpBcAd) => ["B", "C", "A", "D"],
        (SampleTopology.VanDerPauw, SampleRouteSelection.VdpCbAd) => ["C", "B", "A", "D"],
        _ => throw new ArgumentException("Measurement selection does not match the sample topology.", nameof(selection)),
    };
}

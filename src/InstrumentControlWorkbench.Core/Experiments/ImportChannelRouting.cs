using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed class ImportChannelRouting
{
    private static readonly string[][] RoleAliases =
    [
        ["source_plus", "i_plus", "current_plus", "a"], ["source_minus", "i_minus", "current_minus", "b"],
        ["sense_plus", "vxx_plus", "v_plus", "hall_plus", "vxy_plus", "c"], ["sense_minus", "vxx_minus", "v_minus", "hall_minus", "vxy_minus", "d"],
    ];
    public ImportChannelRouting(RoutingProfileCatalog catalog, RoutingConfiguration routing, WiringProfileId wiring, TimeSpan settle, InstrumentId? voltageMeter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(routing); ArgumentNullException.ThrowIfNull(wiring);
        ExperimentDurations.Validate(settle);
        if (!catalog.Channels.SequenceEqual(routing.Channels) || !catalog.Samples.SequenceEqual(routing.Samples))
            throw new ArgumentException("Channel bindings must use the catalog's frozen profiles.");
        Wiring = routing.Wirings.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Id.Value, wiring.Value)) ?? throw new ArgumentException("Channel wiring must be explicitly bound.");
        var layout = catalog.Wirings.SingleOrDefault(w => w.Id == Wiring.Id);
        if (layout is null || layout.SourcePlus != Wiring.SourcePlus || layout.SourceMinus != Wiring.SourceMinus ||
            layout.SensePlus != Wiring.SensePlus || layout.SenseMinus != Wiring.SenseMinus)
            throw new ArgumentException("Bound channel ports differ from the authored wiring layout.");
        if (Wiring.SourceMeter == routing.Matrix) throw new ArgumentException("Channel source and matrix must be distinct instruments.");
        if (voltageMeter is { } meter)
        {
            if (string.IsNullOrWhiteSpace(meter.Value) || meter == Wiring.SourceMeter || meter == routing.Matrix)
                throw new ArgumentException("Channel voltage meter must be a distinct instrument.");
            if (new[] { Wiring.SourcePlus, Wiring.SourceMinus, Wiring.SensePlus, Wiring.SenseMinus }.Distinct().Count() != 4)
                throw new ArgumentException("Independent source/meter wiring needs four distinct instrument ports.");
        }
        Catalog = catalog; Routing = routing; Settle = settle; VoltageMeter = voltageMeter;
    }
    public RoutingProfileCatalog Catalog { get; }
    public RoutingConfiguration Routing { get; }
    public InstrumentWiringProfile Wiring { get; }
    public InstrumentId? VoltageMeter { get; }
    public TimeSpan Settle { get; }
    public ImportChannelRoute Resolve(string query, SensingMode sensingMode, string? measurementMode = null)
    {
        if (!Enum.IsDefined(sensingMode)) throw new ArgumentOutOfRangeException(nameof(sensingMode));
        if (VoltageMeter is not null && measurementMode is not null && !measurementMode.Equals("curr_dc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Independent channel acquisition supports only curr_dc.");
        if (measurementMode is not null && (!Catalog.ModeWirings.TryGetValue(measurementMode, out var expected) || expected != Wiring.Id))
            throw new ArgumentException("Selected channel wiring differs from the measurement mode wiring.");
        var channel = Catalog.ResolveChannel(query); var points = new List<MatrixCrossPoint>();
        for (var i = 0; i < RoleAliases.Length; i++)
        {
            var connection = RoleAliases[i].Select(alias => channel.Connections.FirstOrDefault(c => c.Enabled && c.Column is not null && StringComparer.OrdinalIgnoreCase.Equals(c.Key.Value, alias)))
                .FirstOrDefault(c => c is not null) ?? throw new ArgumentException($"{channel.Id} has no enabled column for {RoleAliases[i][0]}.");
            var port = Wiring.Port((RoutingRole)i);
            if (connection.Slot is { } slot && slot != port.Slot) throw new ArgumentException($"{channel.Id}.{connection.Key}: channel slot differs from wiring slot.");
            points.Add(new(port.Slot, port.Row, connection.Column!.Value));
        }
        var ports = points.Select(p => new MatrixPort(p.Slot, p.Row)).ToArray();
        var terminals = points.Select(p => (p.Slot, p.Column)).ToArray();
        if (new[] { ports[0], ports[2] }.Intersect(new[] { ports[1], ports[3] }).Any() ||
            new[] { terminals[0], terminals[2] }.Intersect(new[] { terminals[1], terminals[3] }).Any())
            throw new ArgumentException("Opposite polarities must use different instrument ports and sample terminals.");
        if (sensingMode == SensingMode.FourWire && (ports.Distinct().Count() != 4 || terminals.Distinct().Count() != 4))
            throw new ArgumentException("Four-wire channels need four independent ports and sample terminals.");
        return new(Wiring.SourceMeter, new(new("import.channel"), Routing.Matrix, points.Distinct(), Settle), VoltageMeter);
    }
}

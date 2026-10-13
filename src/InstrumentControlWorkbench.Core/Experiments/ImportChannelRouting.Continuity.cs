using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed partial class ImportChannelRouting
{
    public MatrixRouteExpectation? ResolveContinuity(ChannelProfileId id, ElectrodePair pair)
    {
        if (VoltageMeter is not null) throw new ArgumentException("Electrode diagnostics require the K2450 Force wiring, without an independent meter.");
        var channel = Catalog.Channels.Single(c => c.Id == id);
        var aliases = pair switch
        {
            ElectrodePair.Source => new[] { new[] { "i_plus", "source_plus" }, new[] { "i_minus", "source_minus" } },
            ElectrodePair.Meter => new[] { new[] { "v_plus", "meter_plus", "sense_plus" }, new[] { "v_minus", "meter_minus", "sense_minus" } },
            ElectrodePair.Bias => new[] { new[] { "bias_plus" }, new[] { "bias_minus" } },
            ElectrodePair.Gate => new[] { new[] { "gate_plus", "gate" }, new[] { "gate_minus", "ground" } },
            _ => throw new ArgumentOutOfRangeException(nameof(pair)),
        };
        // The reference chooses the first enabled authored alias, not a disabled alias.
        var connections = aliases.Select(keys => channel.Connections.FirstOrDefault(c => c.Enabled && c.Column is not null && keys.Contains(c.Key.Value, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (connections.Any(c => c is null)) return null;
        var ports = new[] { Wiring.SourcePlus, Wiring.SourceMinus };
        if (ports[0] == ports[1]) throw new ArgumentException("Force polarities must have distinct instrument ports.");
        if (connections.Where((c, index) => c!.Slot is { } slot && slot != ports[index].Slot).Any()) return null;
        var points = connections.Select((c, index) => new MatrixCrossPoint(ports[index].Slot, ports[index].Row, c!.Column!.Value)).ToArray();
        if (points[0] == points[1]) return null;
        if (points[0].Slot == points[1].Slot && points[0].Column == points[1].Column) throw new ArgumentException("Continuity polarities cannot short the same sample terminal.");
        return new(Routing.Matrix, points);
    }
}

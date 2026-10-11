using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Routing;

// A plan freezes this catalog. Resolution is pure domain work, without file IO
// or global active-profile state that could change during execution.
public sealed class RoutingConfiguration
{
    public RoutingConfiguration(InstrumentId matrix, IEnumerable<ChannelProfile> channels, IEnumerable<InstrumentWiringProfile> wirings, IEnumerable<SampleProfile> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matrix.Value); ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(wirings); ArgumentNullException.ThrowIfNull(samples);
        var c = channels.ToArray(); var w = wirings.ToArray(); var s = samples.ToArray();
        if (c.Length is < 1 or > 64 || c.Any(v => v is null) || c.Select(v => v.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Length ||
            w.Length is < 1 or > 64 || w.Any(v => v is null) || w.Select(v => v.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != w.Length ||
            s.Length is < 1 or > 64 || s.Any(v => v is null) || s.Select(v => v.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Length)
            throw new ArgumentException("Routing catalogs need one to 64 non-null, distinct channels, wirings and samples.");
        Matrix = matrix; Channels = Array.AsReadOnly(c); Wirings = Array.AsReadOnly(w); Samples = Array.AsReadOnly(s);
    }
    public InstrumentId Matrix { get; }
    public IReadOnlyList<ChannelProfile> Channels { get; }
    public IReadOnlyList<InstrumentWiringProfile> Wirings { get; }
    public IReadOnlyList<SampleProfile> Samples { get; }
    public ResolvedSampleRoute Resolve(SampleRoutingBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var sample = Samples.SingleOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, binding.Sample.Value))
            ?? throw new KeyNotFoundException($"Unknown sample {binding.Sample}.");
        var wiring = Wirings.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Id.Value, binding.Wiring.Value))
            ?? throw new KeyNotFoundException($"Unknown instrument wiring {binding.Wiring}.");
        var labels = ResolvedSampleRoute.Labels(sample.Topology, binding.Selection);
        var roles = new List<ResolvedElectrodeRoute>();
        foreach (var role in Enum.GetValues<RoutingRole>())
        {
            var electrode = sample.Electrodes.Single(e => StringComparer.OrdinalIgnoreCase.Equals(e.Label.Value, labels[(int)role]));
            if (!electrode.Enabled) throw new ArgumentException($"Sample electrode {electrode.Label} is disabled.");
            var channel = Channels.SingleOrDefault(c => StringComparer.OrdinalIgnoreCase.Equals(c.Id.Value, electrode.Channel.Value))
                ?? throw new KeyNotFoundException($"Electrode {electrode.Label} links to unknown channel {electrode.Channel}.");
            var connection = channel.Connections.SingleOrDefault(c => StringComparer.OrdinalIgnoreCase.Equals(c.Key.Value, electrode.Connection.Value))
                ?? throw new KeyNotFoundException($"{channel.Id} has no connection {electrode.Connection}.");
            if (!connection.Enabled || connection.Column is null) throw new ArgumentException($"{channel.Id}.{connection.Key} is disabled or has no column.");
            var port = wiring.Port(role);
            if (connection.Slot is { } slot && slot != port.Slot) throw new ArgumentException($"{channel.Id}.{connection.Key}: channel slot differs from the instrument port slot.");
            roles.Add(new(role, electrode.Label, channel.Id, connection.Key, new(port.Slot, port.Row, connection.Column.Value)));
        }
        return new(new(sample.Id, wiring.Id, binding.Selection), sample.Name, sample.Topology, Matrix, wiring.SourceMeter, roles, wiring.VoltageMeter);
    }
    public void ValidateContext(ResolvedSampleRoute context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actual = Resolve(context.Binding);
        if (actual.Matrix != context.Matrix || actual.SourceMeter != context.SourceMeter || actual.VoltageMeter != context.VoltageMeter || actual.SampleName != context.SampleName ||
            actual.Topology != context.Topology || actual.Binding != context.Binding || !actual.Roles.SequenceEqual(context.Roles))
            throw new ArgumentException("Row routing provenance differs from the plan's frozen routing configuration.", nameof(context));
    }
}

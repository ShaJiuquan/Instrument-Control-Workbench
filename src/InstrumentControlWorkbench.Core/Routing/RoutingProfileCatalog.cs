using System.Globalization;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Routing;

// Immutable authored configuration, separate from run-owned executable routing.
public sealed class RoutingProfileCatalog
{
    public RoutingProfileCatalog(ChannelProfileId activeChannel, SampleId activeSample, IEnumerable<ChannelProfile> channels,
        IEnumerable<SampleProfile> samples, IEnumerable<MatrixWiringLayout> wirings, IEnumerable<KeyValuePair<string, WiringProfileId>> modeWirings)
    {
        ArgumentNullException.ThrowIfNull(activeChannel); ArgumentNullException.ThrowIfNull(activeSample);
        ArgumentNullException.ThrowIfNull(channels); ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(wirings); ArgumentNullException.ThrowIfNull(modeWirings);
        var c = channels.ToArray(); var s = samples.ToArray(); var w = wirings.ToArray();
        if (c.Length is < 1 or > 64 || s.Length is < 1 or > 64 || w.Length is < 1 or > 64 ||
            c.Any(p => p is null) || s.Any(p => p is null) || w.Any(p => p is null) ||
            c.Select(p => p.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Length ||
            s.Select(p => p.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Length ||
            w.Select(p => p.Id.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != w.Length)
            throw new ArgumentException("Each routing catalog needs one to 64 distinct non-null entries.");
        if (!c.Any(p => p.Id == activeChannel) || !s.Any(p => p.Id == activeSample)) throw new ArgumentException("Active profile keys must exist.");
        Channels = Array.AsReadOnly(c); Samples = Array.AsReadOnly(s); Wirings = Array.AsReadOnly(w);
        ModeWirings = ExperimentImportBindings.Freeze(modeWirings); ActiveChannel = activeChannel; ActiveSample = activeSample;
        foreach (var mode in ModeWirings)
            if (!w.Any(p => p.Id == mode.Value)) throw new ArgumentException($"Mode {mode.Key} references missing wiring {mode.Value}.");
        foreach (var sample in s)
            foreach (var electrode in sample.Electrodes)
            {
                var channel = c.SingleOrDefault(p => p.Id == electrode.Channel) ?? throw new ArgumentException($"{sample.Id}.{electrode.Label}: missing channel {electrode.Channel}.");
                if (!channel.Connections.Any(p => p.Key == electrode.Connection)) throw new ArgumentException($"{sample.Id}.{electrode.Label}: missing connection {electrode.Connection}.");
            }
    }
    public ChannelProfileId ActiveChannel { get; }
    public SampleId ActiveSample { get; }
    public IReadOnlyList<ChannelProfile> Channels { get; }
    public IReadOnlyList<SampleProfile> Samples { get; }
    public IReadOnlyList<MatrixWiringLayout> Wirings { get; }
    public IReadOnlyDictionary<string, WiringProfileId> ModeWirings { get; }

    public ChannelProfile ResolveChannel(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query); query = query.Trim();
        if (query.Equals("__active__", StringComparison.OrdinalIgnoreCase)) query = ActiveChannel.Value;
        var numberText = query.StartsWith("ch", StringComparison.OrdinalIgnoreCase) ? query[2..] : query;
        var numericKey = uint.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? "channel" + number.ToString(CultureInfo.InvariantCulture) : null;
        var found = Channels.Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.Id.Value, query) ||
            numericKey is not null && StringComparer.OrdinalIgnoreCase.Equals(p.Id.Value, numericKey) ||
            StringComparer.OrdinalIgnoreCase.Equals(p.Name, query) || StringComparer.OrdinalIgnoreCase.Equals(p.OriginalId, query) ||
            p.Alias.Length != 0 && StringComparer.OrdinalIgnoreCase.Equals(p.Alias, query)).ToArray();
        return found.Length == 1 ? found[0] : throw new ArgumentException($"Channel '{query}' is unknown or ambiguous.");
    }

    public ExperimentImportBindings Bind(ExperimentImportBindings devices, InstrumentId matrix,
        IEnumerable<KeyValuePair<WiringProfileId, InstrumentId>> wiringSources, WiringProfileId channelWiring, TimeSpan routeSettle = default, InstrumentId? channelVoltageMeter = null)
    {
        ArgumentNullException.ThrowIfNull(devices); ArgumentNullException.ThrowIfNull(wiringSources); ArgumentNullException.ThrowIfNull(channelWiring);
        if (devices.Samples is not null || devices.ChannelProfiles is not null || devices.Channels.Count != 0)
            throw new ArgumentException("Profile binding needs device-only bindings; competing routing overrides are not accepted.");
        var sources = ExperimentImportBindings.Freeze(wiringSources.Select(p => KeyValuePair.Create(p.Key.Value, p.Value)));
        var bound = sources.Select(p =>
        {
            var layout = Wirings.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Id.Value, p.Key)) ?? throw new ArgumentException($"Unknown wiring {p.Key}.");
            if (!devices.Instruments.Values.Contains(p.Value)) throw new ArgumentException($"Wiring {p.Key} references an unbound source {p.Value}.");
            return new InstrumentWiringProfile(layout.Id, p.Value, layout.SourcePlus, layout.SourceMinus, layout.SensePlus, layout.SenseMinus, layout.Label);
        }).ToArray();
        if (!devices.Instruments.Values.Contains(matrix)) throw new ArgumentException("Matrix must be explicitly bound to an instrument.");
        var routing = new RoutingConfiguration(matrix, Channels, bound, Samples);
        var aliases = Samples.Where(s => s.OriginalId.Length != 0 && !StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, s.OriginalId))
            .Select(s => KeyValuePair.Create(s.OriginalId, s.Id));
        var sampleRouting = new ImportSampleRouting(routing, ModeWirings.Where(p => sources.ContainsKey(p.Value.Value)), aliases, routeSettle);
        return new(devices.Instruments, devices.Axes, devices.Preparations, samples: sampleRouting,
            channelProfiles: new(this, routing, channelWiring, routeSettle, channelVoltageMeter),
            standaloneVoltageMeters: devices.StandaloneVoltageMeters, currentSourcePreparations: devices.CurrentSourcePreparations);
    }
}

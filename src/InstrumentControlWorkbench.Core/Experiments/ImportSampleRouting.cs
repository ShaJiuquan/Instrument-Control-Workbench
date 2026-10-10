using System.Globalization;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

// Frozen import configuration. Resolution is independent of global active files.
public sealed class ImportSampleRouting
{
    public ImportSampleRouting(RoutingConfiguration routing, IEnumerable<KeyValuePair<string, WiringProfileId>> modeWirings,
        IEnumerable<KeyValuePair<string, SampleId>>? aliases = null, TimeSpan routeSettle = default)
    {
        ArgumentNullException.ThrowIfNull(routing); ArgumentNullException.ThrowIfNull(modeWirings);
        ExperimentDurations.Validate(routeSettle);
        Routing = routing; ModeWirings = ExperimentImportBindings.Freeze(modeWirings);
        Aliases = ExperimentImportBindings.Freeze(aliases ?? []); RouteSettle = routeSettle;
        foreach (var wiring in ModeWirings.Values)
            if (!routing.Wirings.Any(w => StringComparer.OrdinalIgnoreCase.Equals(w.Id.Value, wiring.Value)))
                throw new ArgumentException($"Import mode references missing wiring {wiring}.");
        foreach (var alias in Aliases)
        {
            if (!routing.Samples.Any(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, alias.Value.Value)))
                throw new ArgumentException($"Alias {alias.Key} references missing sample {alias.Value}.");
            if (routing.Samples.Any(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, alias.Key) && !StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, alias.Value.Value)))
                throw new ArgumentException($"Alias {alias.Key} shadows another configured sample.");
        }
    }
    public RoutingConfiguration Routing { get; }
    public IReadOnlyDictionary<string, WiringProfileId> ModeWirings { get; }
    public IReadOnlyDictionary<string, SampleId> Aliases { get; }
    public TimeSpan RouteSettle { get; }
    public SampleProfile ResolveSample(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query); query = query.Trim();
        if (Aliases.TryGetValue(query, out var alias)) query = alias.Value;
        var direct = Routing.Samples.SingleOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, query));
        if (direct is not null) return direct;
        if (uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var numeric = Routing.Samples.SingleOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, "sample" + number.ToString(CultureInfo.InvariantCulture)));
            if (numeric is not null) return numeric;
        }
        var named = Routing.Samples.Where(s => StringComparer.OrdinalIgnoreCase.Equals(s.Name, query)).ToArray();
        return named.Length == 1 ? named[0] : throw new ArgumentException($"Sample '{query}' is unknown or has an ambiguous display name.");
    }
}

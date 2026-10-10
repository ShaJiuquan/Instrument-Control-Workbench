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
        Aliases.TryGetValue(query, out var alias);
        var numeric = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? "sample" + number.ToString(CultureInfo.InvariantCulture) : null;
        var found = Routing.Samples.Where(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, query) ||
            alias is not null && StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, alias.Value) ||
            numeric is not null && StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, numeric) ||
            StringComparer.OrdinalIgnoreCase.Equals(s.Name, query)).ToArray();
        return found.Length == 1 ? found[0] : throw new ArgumentException($"Sample '{query}' is unknown or ambiguous.");
    }
}

using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

// Import is a format boundary; it creates ordinary plans, never driver operations.
public interface IExperimentPlanImporter
{
    ExperimentPlan Parse(string text, ExperimentImportBindings bindings);
    Task<ExperimentPlan> LoadAsync(string path, ExperimentImportBindings bindings, CancellationToken token);
}

public sealed record ImportChannelRoute
{
    public ImportChannelRoute(InstrumentId source, RouteMatrixStep route, InstrumentId? voltageMeter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Value);
        ArgumentNullException.ThrowIfNull(route);
        if (voltageMeter is { } meter && (string.IsNullOrWhiteSpace(meter.Value) || meter == source || meter == route.Device)) throw new ArgumentException("Independent route meter must be a distinct instrument.");
        Source = source; Route = route; VoltageMeter = voltageMeter;
    }
    public InstrumentId? VoltageMeter { get; }
    public InstrumentId Source { get; }
    public RouteMatrixStep Route { get; }
}

public sealed class ExperimentImportBindings
{
    public ExperimentImportBindings(IEnumerable<KeyValuePair<string, InstrumentId>> instruments,
        IEnumerable<KeyValuePair<string, AxisControllerDefinition>> axes,
        IEnumerable<SourcePreparation>? preparations = null,
        IEnumerable<KeyValuePair<string, ImportChannelRoute>>? channels = null, ImportSampleRouting? samples = null, ImportChannelRouting? channelProfiles = null, IEnumerable<InstrumentId>? standaloneVoltageMeters = null, IEnumerable<CurrentSourcePreparation>? currentSourcePreparations = null)
    {
        ArgumentNullException.ThrowIfNull(instruments); ArgumentNullException.ThrowIfNull(axes);
        Instruments = Freeze(instruments); Axes = Freeze(axes);
        Channels = Freeze(channels ?? []);
        if (channelProfiles is not null && (Channels.Count != 0 || samples is not null && !ReferenceEquals(samples.Routing, channelProfiles.Routing)))
            throw new ArgumentException("Catalog channel/sample bindings need one frozen routing authority.");
        Samples = samples; ChannelProfiles = channelProfiles;
        var items = (preparations ?? []).ToArray();
        if (items.Length > 64 || items.Any(p => p is null) || items.Select(p => p.Device).Distinct().Count() != items.Length)
            throw new ArgumentException("Import preparations must name at most 64 distinct sources.");
        Preparations = Array.AsReadOnly(items);
        var currents = (currentSourcePreparations ?? []).ToArray();
        if (currents.Any(p => p is null || !Instruments.Values.Contains(p.Device)) || currents.Select(p => p.Device).Concat(items.Select(p => p.Device)).Distinct().Count() != currents.Length + items.Length || currents.Length + items.Length > 64)
            throw new ArgumentException("Current source preparations must be distinct, bounded and explicitly aliased.");
        CurrentSourcePreparations = Array.AsReadOnly(currents);
        var meters = (standaloneVoltageMeters ?? []).ToArray();
        if (meters.Length > 64 || meters.Distinct().Count() != meters.Length || meters.Any(m => !Instruments.Values.Contains(m) || items.Any(p => p.Device == m) || currents.Any(p => p.Device == m)))
            throw new ArgumentException("Standalone meter bindings must reference distinct aliased devices without source preparations.");
        StandaloneVoltageMeters = Array.AsReadOnly(meters);
    }
    public IReadOnlyDictionary<string, InstrumentId> Instruments { get; }
    public IReadOnlyDictionary<string, AxisControllerDefinition> Axes { get; }
    public IReadOnlyDictionary<string, ImportChannelRoute> Channels { get; }
    public IReadOnlyList<SourcePreparation> Preparations { get; }
    public IReadOnlyList<CurrentSourcePreparation> CurrentSourcePreparations { get; }
    public IReadOnlyList<InstrumentId> StandaloneVoltageMeters { get; }
    public ImportSampleRouting? Samples { get; }
    public ImportChannelRouting? ChannelProfiles { get; }
    internal static IReadOnlyDictionary<string, T> Freeze<T>(IEnumerable<KeyValuePair<string, T>> entries)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
            if (entry.Value is null || !result.TryAdd(entry.Key.Trim(), entry.Value) || result.Count > 64)
                throw new ArgumentException("Import bindings need at most 64 distinct, non-null entries.");
        }
        return new ReadOnlyDictionary<string, T>(result);
    }
}

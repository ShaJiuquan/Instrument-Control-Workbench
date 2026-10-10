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
    public ImportChannelRoute(InstrumentId source, RouteMatrixStep route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Value);
        ArgumentNullException.ThrowIfNull(route);
        Source = source; Route = route;
    }
    public InstrumentId Source { get; }
    public RouteMatrixStep Route { get; }
}

public sealed class ExperimentImportBindings
{
    public ExperimentImportBindings(IEnumerable<KeyValuePair<string, InstrumentId>> instruments,
        IEnumerable<KeyValuePair<string, AxisControllerDefinition>> axes,
        IEnumerable<SourcePreparation>? preparations = null,
        IEnumerable<KeyValuePair<string, ImportChannelRoute>>? channels = null, ImportSampleRouting? samples = null, ImportChannelRouting? channelProfiles = null)
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
    }
    public IReadOnlyDictionary<string, InstrumentId> Instruments { get; }
    public IReadOnlyDictionary<string, AxisControllerDefinition> Axes { get; }
    public IReadOnlyDictionary<string, ImportChannelRoute> Channels { get; }
    public IReadOnlyList<SourcePreparation> Preparations { get; }
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

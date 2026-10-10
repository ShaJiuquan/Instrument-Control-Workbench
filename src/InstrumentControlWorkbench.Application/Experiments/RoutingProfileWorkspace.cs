using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record ValidatedRoutingProfileDocument(RoutingProfileCatalog Catalog, string Text);

// Offline edits rebuild and validate the complete immutable catalog; no device operation.
public sealed class RoutingProfileWorkspace(IRoutingProfileDocument documents, IRoutingProfileImporter? importer = null)
{
    public ValidatedRoutingProfileDocument Inspect(RoutingProfileCatalog catalog) => new(catalog, documents.Serialize(catalog));
    public ValidatedRoutingProfileDocument Validate(string text) => Inspect(documents.Parse(text));
    public async Task<ValidatedRoutingProfileDocument> LoadAsync(string path, CancellationToken token) => Inspect(await documents.LoadAsync(path, token));
    public async Task<ValidatedRoutingProfileDocument> SaveAsync(string path, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var document = Validate(text);
        await documents.SaveAsync(path, document.Catalog, token); return document;
    }
    public async Task<ValidatedRoutingProfileDocument> ImportAsync(string channelsPath, string samplesPath, string portsPath, CancellationToken token)
        => Inspect(await (importer ?? throw new InvalidOperationException("No PPMS routing importer is configured.")).LoadAsync(channelsPath, samplesPath, portsPath, token));

    public ValidatedRoutingProfileDocument UpdateChannel(RoutingProfileCatalog catalog, ChannelProfile channel)
        => Rebuild(catalog, channels: Upsert(catalog.Channels, channel, c => c.Id.Value));
    public ValidatedRoutingProfileDocument UpdateSample(RoutingProfileCatalog catalog, SampleProfile sample)
        => Rebuild(catalog, samples: Upsert(catalog.Samples, sample, s => s.Id.Value));
    public ValidatedRoutingProfileDocument UpdateWiring(RoutingProfileCatalog catalog, MatrixWiringLayout wiring)
        => Rebuild(catalog, wirings: Upsert(catalog.Wirings, wiring, w => w.Id.Value));
    public ValidatedRoutingProfileDocument DeleteChannel(RoutingProfileCatalog catalog, ChannelProfileId id)
        => Rebuild(catalog, channels: Remove(catalog.Channels, id.Value, c => c.Id.Value));
    public ValidatedRoutingProfileDocument DeleteSample(RoutingProfileCatalog catalog, SampleId id)
        => Rebuild(catalog, samples: Remove(catalog.Samples, id.Value, s => s.Id.Value));
    public ValidatedRoutingProfileDocument DeleteWiring(RoutingProfileCatalog catalog, WiringProfileId id)
        => Rebuild(catalog, wirings: Remove(catalog.Wirings, id.Value, w => w.Id.Value));
    public ValidatedRoutingProfileDocument SelectActive(RoutingProfileCatalog catalog, ChannelProfileId channel, SampleId sample)
    {
        ArgumentNullException.ThrowIfNull(channel); ArgumentNullException.ThrowIfNull(sample);
        return Rebuild(catalog, activeChannel: channel, activeSample: sample);
    }
    public ValidatedRoutingProfileDocument SetModeWiring(RoutingProfileCatalog catalog, string mode, WiringProfileId? wiring)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        var modes = new Dictionary<string, WiringProfileId>(catalog.ModeWirings, StringComparer.OrdinalIgnoreCase);
        if (wiring is null)
        {
            if (!modes.Remove(mode)) throw new KeyNotFoundException("Unknown mode " + mode);
        }
        else modes[mode] = wiring;
        return Rebuild(catalog, modeWirings: modes);
    }
    private ValidatedRoutingProfileDocument Rebuild(RoutingProfileCatalog catalog, ChannelProfileId? activeChannel = null, SampleId? activeSample = null,
        IEnumerable<ChannelProfile>? channels = null, IEnumerable<SampleProfile>? samples = null, IEnumerable<MatrixWiringLayout>? wirings = null,
        IEnumerable<KeyValuePair<string, WiringProfileId>>? modeWirings = null)
        => Inspect(new(activeChannel ?? catalog.ActiveChannel, activeSample ?? catalog.ActiveSample, channels ?? catalog.Channels,
            samples ?? catalog.Samples, wirings ?? catalog.Wirings, modeWirings ?? catalog.ModeWirings));
    private static T[] Upsert<T>(IReadOnlyList<T> items, T replacement, Func<T, string> key) where T : class
    {
        ArgumentNullException.ThrowIfNull(replacement); var result = items.ToList();
        var index = result.FindIndex(item => StringComparer.OrdinalIgnoreCase.Equals(key(item), key(replacement)));
        if (index < 0) result.Add(replacement); else result[index] = replacement;
        return result.ToArray();
    }
    private static T[] Remove<T>(IReadOnlyList<T> items, string id, Func<T, string> key)
    {
        var result = items.Where(item => !StringComparer.OrdinalIgnoreCase.Equals(key(item), id)).ToArray();
        if (result.Length == items.Count) throw new KeyNotFoundException("Unknown profile " + id);
        return result;
    }
}

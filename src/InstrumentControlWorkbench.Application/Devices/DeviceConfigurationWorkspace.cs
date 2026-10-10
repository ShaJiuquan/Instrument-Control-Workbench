using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed record ValidatedDeviceConfiguration(DeviceConfigurationCatalog Catalog, string Text);
public sealed class DeviceConfigurationWorkspace(IDeviceConfigurationDocument documents, IDeviceConfigurationImporter? importer = null)
{
    public ValidatedDeviceConfiguration Inspect(DeviceConfigurationCatalog catalog) => new(catalog, documents.Serialize(catalog));
    public ValidatedDeviceConfiguration Validate(string text) => Inspect(documents.Parse(text));
    public async Task<ValidatedDeviceConfiguration> LoadAsync(string path, CancellationToken token) => Inspect(await documents.LoadAsync(path, token));
    public async Task<ValidatedDeviceConfiguration> SaveAsync(string path, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var document = Validate(text);
        await documents.SaveAsync(path, document.Catalog, token); return document;
    }
    public async Task<ValidatedDeviceConfiguration> ImportAsync(string path, InstrumentId? primarySource, CancellationToken token)
        => Inspect(await (importer ?? throw new InvalidOperationException("No PPMS connection importer is configured.")).LoadAsync(path, primarySource, token));
    public ValidatedDeviceConfiguration Update(DeviceConfigurationCatalog catalog, DeviceConfiguration device)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(device);
        var devices = catalog.Devices.ToList(); var index = devices.FindIndex(d => d.Id == device.Id);
        if (index < 0) devices.Add(device); else devices[index] = device;
        return Inspect(new(catalog.PrimarySource, devices, catalog.Aliases));
    }
    public ValidatedDeviceConfiguration Delete(DeviceConfigurationCatalog catalog, InstrumentId id)
    {
        ArgumentNullException.ThrowIfNull(catalog); var devices = catalog.Devices.Where(d => d.Id != id).ToArray();
        if (devices.Length == catalog.Devices.Count) throw new KeyNotFoundException("Unknown device instance " + id);
        return Inspect(new(catalog.PrimarySource, devices, catalog.Aliases));
    }
    public ValidatedDeviceConfiguration SelectPrimary(DeviceConfigurationCatalog catalog, InstrumentId? id) => Inspect(new(id, catalog.Devices, catalog.Aliases));
    public ValidatedDeviceConfiguration SetAlias(DeviceConfigurationCatalog catalog, string alias, InstrumentId? id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias); var aliases = new Dictionary<string, InstrumentId>(catalog.Aliases, StringComparer.OrdinalIgnoreCase);
        if (id is { } target) aliases[alias] = target;
        else if (!aliases.Remove(alias)) throw new KeyNotFoundException("Unknown device alias " + alias);
        return Inspect(new(catalog.PrimarySource, catalog.Devices, aliases));
    }
}

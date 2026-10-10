using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Devices;

public interface IDeviceConfigurationDocument
{
    string Serialize(DeviceConfigurationCatalog catalog);
    DeviceConfigurationCatalog Parse(string text);
    Task<DeviceConfigurationCatalog> LoadAsync(string path, CancellationToken token);
    Task SaveAsync(string path, DeviceConfigurationCatalog catalog, CancellationToken token);
}
public interface IDeviceConfigurationImporter
{
    DeviceConfigurationCatalog Parse(string text, InstrumentId? primarySource);
    Task<DeviceConfigurationCatalog> LoadAsync(string path, InstrumentId? primarySource, CancellationToken token);
}

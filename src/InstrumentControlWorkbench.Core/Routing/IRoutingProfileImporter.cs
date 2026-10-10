namespace InstrumentControlWorkbench.Core.Routing;

// Format/file boundary; the Application workspace owns binding and preflight.
public interface IRoutingProfileImporter
{
    RoutingProfileCatalog Parse(string channels, string samples, string instrumentPorts);
    Task<RoutingProfileCatalog> LoadAsync(string channelsPath, string samplesPath, string instrumentPortsPath, CancellationToken token);
}

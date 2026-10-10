using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;

namespace InstrumentControlWorkbench.Application.Devices;

public enum DeviceConfigurationOrigin { SavedFile, ExplicitInitialConfiguration }

public sealed record StartedWorkbench(ValidatedDeviceConfiguration Document, DeviceConfigurationOrigin Origin,
    ConfiguredDeviceRuntime Devices, MeasurementRunService Runs);

// Composition use case: resolve the authored document, then construct one frozen
// device/runtime/engine set. A missing file can use an explicitly supplied initial
// configuration; errors in an existing file never fall back to simulation.
public sealed class DeviceConfigurationStartup(IDeviceConfigurationDocument documents, IConfiguredDeviceFactory factory,
    InstrumentLeaseService leases, NotificationHub notifications)
{
    public async Task<StartedWorkbench> StartAsync(string path, DeviceConfigurationCatalog? initialConfiguration,
        CancellationToken token, IEnumerable<IRunResultListener>? listeners = null, TimeSpan? safetyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        var workspace = new DeviceConfigurationWorkspace(documents);
        ValidatedDeviceConfiguration document;
        var origin = DeviceConfigurationOrigin.SavedFile;
        try { document = await workspace.LoadAsync(path, token); }
        catch (FileNotFoundException) when (initialConfiguration is not null)
        { document = workspace.Inspect(initialConfiguration); origin = DeviceConfigurationOrigin.ExplicitInitialConfiguration; }
        catch (DirectoryNotFoundException) when (initialConfiguration is not null)
        { document = workspace.Inspect(initialConfiguration); origin = DeviceConfigurationOrigin.ExplicitInitialConfiguration; }
        token.ThrowIfCancellationRequested();
        var resultListeners = listeners?.ToArray();
        var runtime = new ConfiguredDeviceRuntime(document.Catalog, factory, leases, notifications);
        var runs = document.Catalog.PrimarySource is null
            ? new MeasurementRunService(runtime.Registry, notifications, resultListeners, safetyTimeout)
            : new MeasurementRunService(runtime.PrimarySource, leases, notifications, resultListeners, safetyTimeout, runtime.Registry);
        return new(document, origin, runtime, runs);
    }
}

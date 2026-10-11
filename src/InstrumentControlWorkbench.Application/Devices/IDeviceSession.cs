using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

// Application boundary shared by manual operations and the experiment engine.
// Driver ports remain in Core; each session owns lifecycle and authorization.
public interface IDeviceSession
{
    DeviceDescriptor Descriptor { get; }
    Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null);
    Task<DeviceStatus> GetDeviceStatusAsync(CancellationToken token, RunId? owner = null);
    Task DisconnectDeviceAsync(CancellationToken token, RunId? owner = null);
    Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null);
    async Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
        => new(await ReadParameterAsync(parameter, token, owner), DateTimeOffset.UtcNow);
    Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null);
    Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null);
    Task ClaimRunAsync(RunId owner, CancellationToken token);
    Task ReleaseRunAsync(RunId owner, CancellationToken token);
}

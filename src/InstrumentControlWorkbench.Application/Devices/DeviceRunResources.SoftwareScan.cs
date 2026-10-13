using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    internal async Task<Quantity> PrepareSoftwareScanAsync(InstrumentId id, Quantity first, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); temperatureScanRequiresRampOff.Add(id); return await ((AxisControllerSession)Get(id)).PrepareSoftwareScanAsync(first, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task ActivateSoftwareScanAsync(InstrumentId id, Quantity first, Quantity last, Quantity nominalRate, Quantity actualRate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); await ((AxisControllerSession)Get(id)).ActivateSoftwareScanAsync(first, last, nominalRate, actualRate, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task<AxisControllerSnapshot> AdvanceSoftwareScanAsync(InstrumentId id, Quantity previous, Quantity next, Quantity actualRate, Quantity goal, Quantity nominalRate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); return await ((AxisControllerSession)Get(id)).AdvanceSoftwareScanAsync(previous, next, actualRate, goal, nominalRate, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task CompleteSoftwareScanAsync(InstrumentId id, Quantity setpoint, Quantity actualRate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); await ((AxisControllerSession)Get(id)).CompleteSoftwareScanAsync(setpoint, actualRate, Owner, token); temperatureScanRequiresRampOff.Remove(id); }
        finally { gate.Release(); }
    }
}

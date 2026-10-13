using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    private readonly HashSet<InstrumentId> temperatureScanRequiresRampOff = [];
    private readonly HashSet<InstrumentId> heldTemperatureScans = [];
    internal async Task PrepareTemperatureScanAsync(InstrumentId id, Quantity first, Quantity rate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); temperatureScanRequiresRampOff.Add(id); await ((AxisControllerSession)Get(id)).PrepareTemperatureScanAsync(first, rate, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task<AxisControllerSnapshot> ObserveTemperatureScanAsync(InstrumentId id, Quantity target, Quantity rate, bool ramp, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await RequireStoppedTaskFinishedAsync(id); return await ((AxisControllerSession)Get(id)).ObserveTemperatureScanAsync(target, rate, ramp, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task StartTemperatureScanAsync(InstrumentId id, Quantity first, Quantity last, Quantity rate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); await ((AxisControllerSession)Get(id)).StartTemperatureScanAsync(first, last, rate, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task CompleteTemperatureScanAsync(InstrumentId id, Quantity last, Quantity rate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await PrepareMutationAsync(id); touched.Add(id); await ((AxisControllerSession)Get(id)).CompleteTemperatureScanAsync(last, rate, Owner, token); temperatureScanRequiresRampOff.Remove(id); }
        finally { gate.Release(); }
    }
}

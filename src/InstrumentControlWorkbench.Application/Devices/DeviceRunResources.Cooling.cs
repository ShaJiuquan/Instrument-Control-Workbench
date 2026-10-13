using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    private readonly HashSet<InstrumentId> coolingRequiresOff = [];
    internal async Task<Quantity> BeginCoolingAsync(InstrumentId id, Quantity target, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await PrepareMutationAsync(id); touched.Add(id); coolingRequiresOff.Add(id);
            return await ((AxisControllerSession)Get(id)).BeginCoolingAsync(target, Owner, token);
        }
        finally { gate.Release(); }
    }
    internal async Task<AxisControllerSnapshot> ObserveCoolingAsync(InstrumentId id, Quantity target, Quantity rate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireActive(); await RequireStoppedTaskFinishedAsync(id); return await ((AxisControllerSession)Get(id)).ObserveCoolingAsync(target, rate, Owner, token); }
        finally { gate.Release(); }
    }
    internal async Task CompleteCoolingAsync(InstrumentId id, Quantity target, Quantity rate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireActive(); await PrepareMutationAsync(id); touched.Add(id);
            await ((AxisControllerSession)Get(id)).CompleteCoolingAsync(target, rate, Owner, token); coolingRequiresOff.Remove(id);
        }
        finally { gate.Release(); }
    }
}

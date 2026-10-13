using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class AxisControllerSession
{
    internal async Task<Quantity> PrepareSoftwareScanAsync(Quantity first, RunId owner, CancellationToken token)
    {
        var state = await BeginSoftwareTemperatureRampAsync(first, true, null, owner, token);
        await AdvanceSoftwareScanAsync(state.Start, first, state.Rate, first, state.Rate, owner, token); return state.Rate;
    }
    internal async Task ActivateSoftwareScanAsync(Quantity first, Quantity last, Quantity nominalRate, Quantity actualRate, RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var snapshot = await ObserveTemperatureScanCoreAsync(first, actualRate, false, token, publish: false);
            softwareTarget = last; softwareRate = nominalRate; stabilityObserver.Reset(); await PublishAsync(snapshot, token, new(snapshot, new(false, true), stabilitySettings, softwareTarget));
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task<AxisControllerSnapshot> AdvanceSoftwareScanAsync(Quantity previous, Quantity next, Quantity actualRate, Quantity goal, Quantity nominalRate, RunId owner, CancellationToken token)
    {
        Definition.Target.ValidateWrite(next);
        await operations.EnterAsync(token, owner);
        try
        {
            _ = await ObserveTemperatureScanCoreAsync(previous, actualRate, false, token, publish: false);
            if (next != previous) await port.SetTargetAsync(next, token);
            var snapshot = await ObserveTemperatureScanCoreAsync(next, actualRate, false, token, publish: false);
            var done = next == SoftwareTemperatureTrajectory.Round(goal);
            softwareTarget = done ? null : goal; softwareRate = done ? null : nominalRate;
            await PublishAsync(snapshot, token, new(snapshot, new(false, true), stabilitySettings, softwareTarget)); return snapshot;
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task CompleteSoftwareScanAsync(Quantity setpoint, Quantity actualRate, RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try { var snapshot = await ObserveTemperatureScanCoreAsync(setpoint, actualRate, false, token, publish: false); ResetTemperatureRuntime(); await PublishAsync(snapshot, token, new(snapshot, new(false, true), stabilitySettings)); }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class AxisControllerSession
{
    internal async Task<Quantity> BeginCoolingAsync(Quantity target, RunId owner, CancellationToken token)
    {
        TemperatureRampPath.ValidateTemperature(target); Definition.Target.ValidateWrite(target);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = SupportsTemperatureConfiguration ? (ITemperatureConfigurationPort)port : throw new NotSupportedException("Passive cooling needs independent ramp/control configuration.");
            var before = await port.GetSnapshotAsync(token); if (!before.Connected) throw new InvalidOperationException("Cooling controller disconnected.");
            // Disable heating before programming a low target; no stale target is enabled.
            await temperature.ConfigureControlAsync(new(false, false), null, token);
            if (await temperature.ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(false, false)) throw new InvalidOperationException("Cooling did not confirm ramp/control OFF.");
            await port.SetTargetAsync(target, token); ResetTemperatureRuntime();
            _ = await ObserveCoolingCoreAsync(target, before.Rate, token); return before.Rate;
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    private async Task<AxisControllerSnapshot> ObserveCoolingCoreAsync(Quantity target, Quantity rate, CancellationToken token)
    {
        var temperature = (ITemperatureConfigurationPort)port;
        var expected = new TemperatureControlConfiguration(false, false);
        if (await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Cooling ramp/control flags changed.");
        var snapshot = await port.GetSnapshotAsync(token);
        Definition.Readback.Validate(snapshot.Readback); Definition.Target.Validate(snapshot.Target); Definition.Rate.Validate(snapshot.Rate);
        if (!snapshot.Connected || snapshot.Moving || snapshot.Target != target || snapshot.Rate != rate || await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Cooling observation has changed target/rate/control or disconnected state.");
        await PublishAsync(snapshot, token, new(snapshot, expected, stabilitySettings)); return snapshot;
    }
    internal async Task<AxisControllerSnapshot> ObserveCoolingAsync(Quantity target, Quantity rate, RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try { return await ObserveCoolingCoreAsync(target, rate, token); }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task CompleteCoolingAsync(Quantity target, Quantity rate, RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            _ = await ObserveCoolingCoreAsync(target, rate, token);
            var temperature = (ITemperatureConfigurationPort)port;
            await temperature.ConfigureControlAsync(new(false, true), null, token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Target != target || snapshot.Rate != rate || await temperature.ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(false, true)) throw new InvalidOperationException("Cooling completion did not confirm control ON and ramp OFF.");
            await PublishAsync(snapshot, token, new(snapshot, new(false, true), stabilitySettings));
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task<DeviceStatus> StopCoolingAsync(RunId owner, CancellationToken token)
    {
        Exception? disableFailure = null;
        try
        {
            await operations.EnterAsync(token, owner);
            try
            {
                var temperature = (ITemperatureConfigurationPort)port;
                await temperature.ConfigureControlAsync(new(false, false), null, token);
                if (await temperature.ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(false, false)) throw new InvalidOperationException("Cooling cleanup did not confirm both flags OFF.");
            }
            finally { operations.Exit(); }
        }
        catch (Exception error) { disableFailure = error; }
        DeviceStatus status;
        try { status = await StopDeviceAsync(token, owner); }
        catch (Exception error) when (disableFailure is not null) { throw new AggregateException("Cooling OFF and hold both failed.", disableFailure, error); }
        if (disableFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disableFailure).Throw();
        return status;
    }
}

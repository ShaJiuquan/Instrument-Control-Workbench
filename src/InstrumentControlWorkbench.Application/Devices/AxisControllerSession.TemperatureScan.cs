using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class AxisControllerSession
{
    internal async Task PrepareTemperatureScanAsync(Quantity first, Quantity rate, RunId owner, CancellationToken token)
    {
        TemperatureRampPath.ValidateTemperature(first); TemperatureRampPath.ValidateRate(rate); Definition.Target.ValidateWrite(first); Definition.Rate.ValidateWrite(rate);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = SupportsTemperatureConfiguration ? (ITemperatureConfigurationPort)port : throw new NotSupportedException("Hardware scan requires independent temperature configuration.");
            var before = await port.GetSnapshotAsync(token); if (!before.Connected) throw new InvalidOperationException("Temperature controller disconnected.");
            Definition.Target.ValidateWrite(before.Readback);
            await temperature.PrepareSoftwareRampAsync(before.Readback, token);
            var parked = await port.GetSnapshotAsync(token);
            if (!parked.Connected || parked.Target != before.Readback || await temperature.ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(false, true)) throw new InvalidOperationException("Ramp scan did not park live temperature before enabling control.");
            await temperature.ConfigureControlAsync(new(false, true), rate, token);
            await port.SetTargetAsync(first, token); ResetTemperatureRuntime();
            _ = await ObserveTemperatureScanCoreAsync(first, rate, false, token);
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    private async Task<AxisControllerSnapshot> ObserveTemperatureScanCoreAsync(Quantity target, Quantity rate, bool ramp, CancellationToken token)
    {
        var temperature = (ITemperatureConfigurationPort)port; var expected = new TemperatureControlConfiguration(ramp, true);
        if (await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Temperature scan control/ramp configuration changed.");
        var snapshot = await port.GetSnapshotAsync(token); Definition.Readback.Validate(snapshot.Readback); Definition.Target.Validate(snapshot.Target); Definition.Rate.Validate(snapshot.Rate);
        if (!snapshot.Connected || snapshot.Target != target || snapshot.Rate != rate || await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Temperature scan target/rate/control drifted or disconnected.");
        await PublishAsync(snapshot, token, new(snapshot, expected, stabilitySettings)); return snapshot;
    }
    internal async Task<AxisControllerSnapshot> ObserveTemperatureScanAsync(Quantity target, Quantity rate, bool ramp, RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try { return await ObserveTemperatureScanCoreAsync(target, rate, ramp, token); }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task StartTemperatureScanAsync(Quantity first, Quantity last, Quantity rate, RunId owner, CancellationToken token)
    {
        Definition.Target.ValidateWrite(last);
        await operations.EnterAsync(token, owner);
        try
        {
            _ = await ObserveTemperatureScanCoreAsync(first, rate, false, token);
            var temperature = (ITemperatureConfigurationPort)port;
            await temperature.ConfigureControlAsync(new(true, true), rate, token);
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Target != first || before.Rate != rate || await temperature.ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(true, true)) throw new InvalidOperationException("Hardware ramp did not confirm before final target.");
            await port.SetTargetAsync(last, token); _ = await ObserveTemperatureScanCoreAsync(last, rate, true, token);
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task CompleteTemperatureScanAsync(Quantity last, Quantity rate, RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            _ = await ObserveTemperatureScanCoreAsync(last, rate, true, token);
            await ((ITemperatureConfigurationPort)port).ConfigureControlAsync(new(false, true), null, token);
            _ = await ObserveTemperatureScanCoreAsync(last, rate, false, token);
        }
        catch { faulted = true; ResetTemperatureRuntime(); throw; }
        finally { operations.Exit(); }
    }
    internal async Task<DeviceStatus> StopTemperatureScanAsync(RunId owner, CancellationToken token)
    {
        Exception? failure = null;
        try { await ConfigureTemperatureAsync(TemperatureControlAction.RampOff, null, token, owner); }
        catch (Exception error) { failure = error; }
        DeviceStatus result;
        try { result = await StopDeviceAsync(token, owner); }
        catch (Exception error) when (failure is not null) { throw new AggregateException("Ramp OFF and hold failed.", failure, error); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}

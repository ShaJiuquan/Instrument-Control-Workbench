using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class AxisControllerSession
{
    internal async Task<Quantity> BeginSoftwareTemperatureRampAsync(Quantity target, RunId owner, CancellationToken token)
    {
        TemperatureRampPath.ValidateTemperature(target); Definition.Target.ValidateWrite(target);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort();
            // Read and validate the physical origin before changing control mode.
            // The port parks at that origin before enabling control.
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected) throw new InvalidOperationException("Temperature controller is disconnected.");
            Definition.Readback.Validate(snapshot.Readback); TemperatureRampPath.ValidateTemperature(snapshot.Readback);
            Definition.Target.ValidateWrite(snapshot.Readback);
            await temperature.PrepareSoftwareRampAsync(snapshot.Readback, token);
            var configuration = await temperature.ReadControlConfigurationAsync(token);
            var confirmed = await port.GetSnapshotAsync(token);
            if (configuration != new TemperatureControlConfiguration(false, true) || !confirmed.Connected || confirmed.Target != snapshot.Readback || await temperature.ReadControlConfigurationAsync(token) != configuration)
                throw new InvalidOperationException("Temperature control did not confirm its initial hold, software mode and enabled control.");
            await PublishAsync(confirmed, token); return snapshot.Readback;
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task WriteSoftwareTemperatureTargetAsync(Quantity target, RunId owner, CancellationToken token)
    {
        TemperatureRampPath.ValidateTemperature(target); Definition.Target.ValidateWrite(target);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort(); var expected = new TemperatureControlConfiguration(false, true);
            if (await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Temperature software-ramp configuration changed before a target write.");
            await port.SetTargetAsync(target, token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Target != target || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Temperature target or control configuration changed during a write.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task<TemperatureControlConfiguration> CaptureTemperatureConfigurationAsync(RunId owner, CancellationToken token)
    {
        await operations.EnterAsync(token, owner);
        try { return await TemperaturePort().ReadControlConfigurationAsync(token); }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task RestoreTemperatureTargetAsync(Quantity target, TemperatureControlConfiguration expected, RunId owner, CancellationToken token)
    {
        Definition.Target.ValidateWrite(target); await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort();
            if (await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Temperature configuration changed while paused; resume refused.");
            await port.SetTargetAsync(target, token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Target != target || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Temperature configuration changed during restoration.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    private ITemperatureControlPort TemperaturePort() => Definition.SupportsSoftwareTemperatureRamp && port is ITemperatureControlPort temperature
        ? temperature : throw new NotSupportedException("This controller has no software temperature-ramp capability.");
}

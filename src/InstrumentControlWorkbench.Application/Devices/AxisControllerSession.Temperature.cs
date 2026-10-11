using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class AxisControllerSession
{
    private TemperatureStabilitySettings stabilitySettings = TemperatureStabilitySettings.Default;

    internal async Task<TemperatureStabilitySettings> ConfigureTemperatureStabilityAsync(TemperatureStabilityChange change, CancellationToken token, RunId? owner = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        await operations.EnterAsync(token, owner);
        try { _ = TemperaturePort(); token.ThrowIfCancellationRequested(); stabilitySettings = change.Apply(stabilitySettings); return stabilitySettings; }
        finally { operations.Exit(); }
    }

    internal async Task<TemperatureStabilityObservation> ReadTemperatureStabilityAsync(CancellationToken token, RunId? owner = null)
    {
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort();
            var before = await temperature.ReadControlConfigurationAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (await temperature.ReadControlConfigurationAsync(token) != before)
                throw new InvalidOperationException("Temperature control flags changed during stability observation.");
            if (snapshot.Connected) { Definition.Target.Validate(snapshot.Target); Definition.Readback.Validate(snapshot.Readback); }
            await PublishAsync(snapshot, token);
            return new(snapshot, before, stabilitySettings);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

    public bool SupportsTemperatureConfiguration => Definition.SupportsSoftwareTemperatureRamp && port is ITemperatureConfigurationPort;

    internal async Task ConfigureTemperatureAsync(TemperatureControlAction action, Quantity? rate, CancellationToken token, RunId? owner = null)
    {
        // The same validated command contract is used for manual and plan calls.
        _ = new TemperatureControlStep(new("temperature.configuration"), Descriptor.Id, action, rate);
        if (rate is { } requested) Definition.Rate.ValidateWrite(requested);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = SupportsTemperatureConfiguration ? (ITemperatureConfigurationPort)port
                : throw new NotSupportedException("Controller has no independent temperature-configuration capability.");
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected) throw new InvalidOperationException("Temperature controller is disconnected.");
            Definition.Target.Validate(before.Target); Definition.Rate.Validate(before.Rate);
            var original = await temperature.ReadControlConfigurationAsync(token);
            var expected = action switch
            {
                TemperatureControlAction.RampOn => original with { HardwareRampEnabled = true },
                TemperatureControlAction.RampOff => original with { HardwareRampEnabled = false },
                TemperatureControlAction.ControlOn => original with { ControlEnabled = true },
                TemperatureControlAction.ControlOff => original with { ControlEnabled = false },
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
            await temperature.ConfigureControlAsync(expected, rate, token);
            var confirmed = await temperature.ReadControlConfigurationAsync(token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Target != before.Target || snapshot.Rate != (rate ?? before.Rate) ||
                confirmed != expected || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Temperature command did not confirm its target, rate and independent control flags.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }

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
    internal async Task RestoreTemperatureTargetAsync(Quantity target, TemperatureControlConfiguration expected, Quantity expectedRate, RunId owner, CancellationToken token)
    {
        Definition.Target.ValidateWrite(target); await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort();
            if (await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Temperature configuration changed while paused; resume refused.");
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected || before.Rate != expectedRate) throw new InvalidOperationException("Temperature rate changed while paused; resume refused.");
            await port.SetTargetAsync(target, token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Target != target || snapshot.Rate != expectedRate || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Temperature configuration changed during restoration.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    private ITemperatureControlPort TemperaturePort() => Definition.SupportsSoftwareTemperatureRamp && port is ITemperatureControlPort temperature
        ? temperature : throw new NotSupportedException("This controller has no software temperature-ramp capability.");
}

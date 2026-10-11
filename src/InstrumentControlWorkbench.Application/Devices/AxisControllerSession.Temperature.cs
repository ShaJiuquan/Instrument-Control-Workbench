using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

internal sealed record SoftwareTemperatureRampState(Quantity Start, TemperatureControlConfiguration Control, Quantity Rate);

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

    internal async Task<SoftwareTemperatureRampState> BeginSoftwareTemperatureRampAsync(Quantity target, bool enableControl, RunId owner, CancellationToken token)
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
            Definition.Target.ValidateWrite(snapshot.Readback); Definition.Rate.Validate(snapshot.Rate);
            TemperatureControlConfiguration expected;
            if (enableControl)
            {
                await temperature.PrepareSoftwareRampAsync(snapshot.Readback, token);
                expected = new(false, true);
            }
            else expected = await PreparePreservedTemperatureAsync(snapshot, hardwareRamp: false, rate: null, token);
            var configuration = await temperature.ReadControlConfigurationAsync(token);
            var confirmed = await port.GetSnapshotAsync(token);
            if (configuration != expected || !confirmed.Connected || confirmed.Target != snapshot.Readback || confirmed.Rate != snapshot.Rate || await temperature.ReadControlConfigurationAsync(token) != configuration)
                throw new InvalidOperationException("Temperature control did not confirm its initial hold, software mode, rate and expected control flags.");
            await PublishAsync(confirmed, token); return new(snapshot.Readback, expected, snapshot.Rate);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    internal async Task WriteSoftwareTemperatureTargetAsync(Quantity target, SoftwareTemperatureRampState state, RunId owner, CancellationToken token)
    {
        TemperatureRampPath.ValidateTemperature(target); Definition.Target.ValidateWrite(target);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort(); var expected = state.Control;
            if (expected.HardwareRampEnabled) throw new ArgumentException("Software temperature ramp requires hardware ramp off.", nameof(expected));
            if (await temperature.ReadControlConfigurationAsync(token) != expected) throw new InvalidOperationException("Temperature software-ramp configuration changed before a target write.");
            await port.SetTargetAsync(target, token);
            var snapshot = await port.GetSnapshotAsync(token);
            if (!snapshot.Connected || snapshot.Target != target || snapshot.Rate != state.Rate || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Temperature target, rate or control configuration changed during a write.");
            await PublishAsync(snapshot, token);
        }
        catch { faulted = true; throw; }
        finally { operations.Exit(); }
    }
    // Caller owns the operation gate. Park a live target before changing ramp
    // mode, preserving the actual control flag rather than trusting a default.
    private async Task<TemperatureControlConfiguration> PreparePreservedTemperatureAsync(AxisControllerSnapshot before, bool hardwareRamp, Quantity? rate, CancellationToken token)
    {
        var temperature = SupportsTemperatureConfiguration ? (ITemperatureConfigurationPort)port
            : throw new NotSupportedException("Preserved-control temperature sets require independent configuration capability.");
        var original = await temperature.ReadControlConfigurationAsync(token);
        await port.SetTargetAsync(before.Readback, token);
        var parked = await port.GetSnapshotAsync(token);
        if (!parked.Connected || parked.Target != before.Readback || parked.Rate != before.Rate || await temperature.ReadControlConfigurationAsync(token) != original)
            throw new InvalidOperationException("Temperature parking did not preserve target, rate and control flags.");
        var expected = original with { HardwareRampEnabled = hardwareRamp };
        await temperature.ConfigureControlAsync(expected, rate, token);
        var configured = await port.GetSnapshotAsync(token);
        if (!configured.Connected || configured.Target != before.Readback || configured.Rate != (rate ?? before.Rate) ||
            await temperature.ReadControlConfigurationAsync(token) != expected)
            throw new InvalidOperationException("Temperature ramp configuration did not preserve control and parked target.");
        return expected;
    }

    internal async Task SetHardwareTemperatureTargetAsync(Quantity target, Quantity rate, bool enableControl, RunId owner, CancellationToken token)
    {
        TemperatureRampPath.ValidateTemperature(target); TemperatureRampPath.ValidateRate(rate);
        Definition.Target.ValidateWrite(target); Definition.Rate.ValidateWrite(rate);
        await operations.EnterAsync(token, owner);
        try
        {
            var temperature = TemperaturePort();
            var before = await port.GetSnapshotAsync(token);
            if (!before.Connected) throw new InvalidOperationException("Temperature controller is disconnected.");
            TemperatureRampPath.ValidateTemperature(before.Readback); Definition.Readback.Validate(before.Readback); Definition.Target.ValidateWrite(before.Readback); Definition.Rate.Validate(before.Rate);
            TemperatureControlConfiguration expected;
            if (enableControl)
            {
                // The adapter parks the live origin before it enables control.
                await temperature.PrepareSoftwareRampAsync(before.Readback, token);
                var parked = await port.GetSnapshotAsync(token);
                if (!parked.Connected || parked.Target != before.Readback || await temperature.ReadControlConfigurationAsync(token) != new TemperatureControlConfiguration(false, true))
                    throw new InvalidOperationException("Temperature control did not confirm its initial parked state.");
                await temperature.SetHardwareRampAsync(rate, token); expected = new(true, true);
            }
            else expected = await PreparePreservedTemperatureAsync(before, hardwareRamp: true, rate, token);
            var ready = await port.GetSnapshotAsync(token);
            if (!ready.Connected || ready.Target != before.Readback || ready.Rate != rate || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Hardware temperature ramp configuration did not confirm before target write.");
            await port.SetTargetAsync(target, token);
            var confirmed = await port.GetSnapshotAsync(token);
            if (!confirmed.Connected || confirmed.Target != target || confirmed.Rate != rate || await temperature.ReadControlConfigurationAsync(token) != expected)
                throw new InvalidOperationException("Hardware temperature target, rate or control flags changed during write.");
            await PublishAsync(confirmed, token);
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

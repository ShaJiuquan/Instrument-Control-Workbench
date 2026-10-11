namespace InstrumentControlWorkbench.Core.Devices;

public sealed record TemperatureControlConfiguration(bool HardwareRampEnabled, bool ControlEnabled);

public enum TemperatureControlAction { RampOn, RampOff, ControlOn, ControlOff }

// Optional independent configuration capability. Existing ramp-only ports do
// not implicitly acquire support for enabling/disabling heater control.
public interface ITemperatureConfigurationPort : ITemperatureControlPort
{
    // Apply the supplied freshly observed configuration without changing the
    // target; null rate preserves the actual rate register.
    Task ConfigureControlAsync(TemperatureControlConfiguration configuration, Parameters.Quantity? rate, CancellationToken token);
}

// Default preparation confirms hardware-ramp-off and control-on. Optional
// independent configuration permits preserving the observed control flag;
// application orchestration owns that path, protocol details stay in adapters.
public interface ITemperatureControlPort : IAxisControllerPort
{
    // Park at the supplied live origin with hardware ramp disabled before
    // enabling temperature control; never enable a stale stored target.
    Task PrepareSoftwareRampAsync(Parameters.Quantity initialTarget, CancellationToken token);
    Task SetHardwareRampAsync(Parameters.Quantity rate, CancellationToken token);
    Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token);
}

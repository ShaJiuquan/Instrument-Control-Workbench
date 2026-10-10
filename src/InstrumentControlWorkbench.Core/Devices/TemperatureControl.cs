namespace InstrumentControlWorkbench.Core.Devices;

public sealed record TemperatureControlConfiguration(bool HardwareRampEnabled, bool ControlEnabled);

// Real temperature-control boundary: software setpoints require confirmed
// hardware-ramp-off and control-on; protocol details stay in adapters.
public interface ITemperatureControlPort : IAxisControllerPort
{
    // Park at the supplied live origin with hardware ramp disabled before
    // enabling temperature control; never enable a stale stored target.
    Task PrepareSoftwareRampAsync(Parameters.Quantity initialTarget, CancellationToken token);
    Task SetHardwareRampAsync(Parameters.Quantity rate, CancellationToken token);
    Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token);
}

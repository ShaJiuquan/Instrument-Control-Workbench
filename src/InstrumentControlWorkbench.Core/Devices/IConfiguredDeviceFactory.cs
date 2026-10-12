using InstrumentControlWorkbench.Core.Instruments.Ports;

namespace InstrumentControlWorkbench.Core.Devices;

// Validation and construction perform no IO. Connections belong to run/manual sessions.
public interface IConfiguredDeviceFactory
{
    void Validate(DeviceConfiguration configuration);
    ConfiguredDevicePorts Create(DeviceConfiguration configuration);
}
public sealed class ConfiguredDevicePorts
{
    public ConfiguredDevicePorts(ISourceMeterPort source) { ArgumentNullException.ThrowIfNull(source); SourceMeter = source; }
    public ConfiguredDevicePorts(IAxisControllerPort axis) { ArgumentNullException.ThrowIfNull(axis); Axis = axis; }
    public ConfiguredDevicePorts(IMatrixSwitchPort matrix) { ArgumentNullException.ThrowIfNull(matrix); Matrix = matrix; }
    public ConfiguredDevicePorts(IK2182MeterPort meter) { ArgumentNullException.ThrowIfNull(meter); Meter = meter; }
    public ConfiguredDevicePorts(IK6221CurrentSourcePort source) { ArgumentNullException.ThrowIfNull(source); CurrentSource = source; }
    public ConfiguredDevicePorts(ISr830MeterPort meter) { ArgumentNullException.ThrowIfNull(meter); LockIn = meter; }
    public ISr830MeterPort? LockIn { get; }
    public IK6221CurrentSourcePort? CurrentSource { get; }
    public IK2182MeterPort? Meter { get; }
    public ISourceMeterPort? SourceMeter { get; }
    public IAxisControllerPort? Axis { get; }
    public IMatrixSwitchPort? Matrix { get; }
    public DeviceDescriptor Descriptor => SourceMeter?.Descriptor ?? CurrentSource?.Descriptor ?? Meter?.Descriptor ?? LockIn?.Descriptor ?? Axis?.Definition.Device ?? Matrix!.Definition.Device;
}

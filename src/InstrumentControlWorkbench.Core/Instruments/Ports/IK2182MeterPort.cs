using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments.Ports;

// Passive instrument lifecycle: no source setpoint, output or SMU snapshot.
public interface IK2182MeterPort
{
    DeviceDescriptor Descriptor { get; }
    Task<K2182MeterSnapshot> ConnectAsync(CancellationToken token);
    Task<K2182MeterSnapshot> GetSnapshotAsync(CancellationToken token);
    Task ConfigureAsync(Keithley2182Settings settings, CancellationToken token);
    Task<K2182VoltageReading> ReadAsync(CancellationToken token);
    Task AbortAcquisitionAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
public sealed record K2182MeterSnapshot
{
    public K2182MeterSnapshot(bool connected, bool acquiring, Keithley2182Settings? settings, DateTimeOffset timestamp)
    {
        if (connected && settings is null || acquiring && !connected) throw new ArgumentException("Connected voltage meter requires observed settings; acquisition requires connection.");
        Connected = connected; Acquiring = acquiring; Settings = settings; Timestamp = timestamp;
    }
    public bool Connected { get; }
    public bool Acquiring { get; }
    public Keithley2182Settings? Settings { get; }
    public DateTimeOffset Timestamp { get; }
}
public sealed record K2182VoltageReading(Quantity Voltage, DateTimeOffset CapturedAt);

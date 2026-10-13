using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments.Ports;

// Passive picoammeter: no source setpoint/output, channel selection or SMU status.
public interface IK6485MeterPort
{
    DeviceDescriptor Descriptor { get; }
    Task<K6485MeterSnapshot> ConnectAsync(CancellationToken token);
    Task<K6485MeterSnapshot> GetSnapshotAsync(CancellationToken token);
    Task ConfigureAsync(Keithley6485Settings settings, CancellationToken token);
    Task<K6485CurrentReading> ReadAsync(CancellationToken token);
    Task AbortAcquisitionAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
public sealed record K6485MeterSnapshot
{
    public K6485MeterSnapshot(bool connected, bool acquiring, Keithley6485Settings? settings, DateTimeOffset timestamp)
    {
        if (connected && settings is null || acquiring && !connected) throw new ArgumentException("Connected picoammeter requires observed settings; acquisition requires connection.");
        Connected = connected; Acquiring = acquiring; Settings = settings; Timestamp = timestamp;
    }
    public bool Connected { get; }
    public bool Acquiring { get; }
    public Keithley6485Settings? Settings { get; }
    public DateTimeOffset Timestamp { get; }
}
public sealed record K6485CurrentReading(Quantity Current, DateTimeOffset CapturedAt);

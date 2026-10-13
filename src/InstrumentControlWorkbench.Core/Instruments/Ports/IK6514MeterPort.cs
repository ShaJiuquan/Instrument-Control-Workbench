using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments.Ports;

// Electrometer acquisition lifecycle; intrinsic ohms excitation/guard is not an SMU output.
public interface IK6514MeterPort
{
    DeviceDescriptor Descriptor { get; }
    Task<K6514MeterSnapshot> ConnectAsync(CancellationToken token);
    Task<K6514MeterSnapshot> GetSnapshotAsync(CancellationToken token);
    Task SelectFunctionAsync(ElectrometerFunction function, CancellationToken token);
    Task ConfigureAsync(Keithley6514Settings settings, CancellationToken token);
    Task<K6514Reading> ReadAsync(CancellationToken token);
    Task AbortAcquisitionAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
public sealed record K6514MeterSnapshot
{
    public K6514MeterSnapshot(bool connected, bool acquiring, Keithley6514Settings? settings, DateTimeOffset timestamp)
    {
        if (connected && settings is null || acquiring && !connected) throw new ArgumentException("Connected picoammeter requires observed settings; acquisition requires connection.");
        Connected = connected; Acquiring = acquiring; Settings = settings; Timestamp = timestamp;
    }
    public bool Connected { get; }
    public bool Acquiring { get; }
    public Keithley6514Settings? Settings { get; }
    public DateTimeOffset Timestamp { get; }
}
public sealed record K6514Reading(ElectrometerFunction Function, Quantity Value, DateTimeOffset CapturedAt);

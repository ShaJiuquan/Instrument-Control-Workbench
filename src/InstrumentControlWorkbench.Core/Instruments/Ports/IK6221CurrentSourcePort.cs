using InstrumentControlWorkbench.Core.Devices;

namespace InstrumentControlWorkbench.Core.Instruments.Ports;

public interface IK6221CurrentSourcePort
{
    DeviceDescriptor Descriptor { get; }
    Task<K6221CurrentSourceSnapshot> ConnectAsync(CancellationToken token);
    Task<K6221CurrentSourceSnapshot> GetSnapshotAsync(CancellationToken token);
    Task ConfigureAsync(Keithley6221Settings settings, CancellationToken token);
    Task SetOutputAsync(bool enabled, CancellationToken token);
    // A real adapter must reject unsupported active source modes and cancel
    // autonomous output actions before confirming fresh output-off.
    Task SafeStopAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
public sealed record K6221CurrentSourceSnapshot
{
    public K6221CurrentSourceSnapshot(bool connected, bool outputEnabled, Keithley6221Settings? settings, DateTimeOffset timestamp)
    {
        if (connected && settings is null || outputEnabled && !connected) throw new ArgumentException("Connected current source requires observed settings; enabled output requires connection.");
        Connected = connected; OutputEnabled = outputEnabled; Settings = settings; Timestamp = timestamp;
    }
    public bool Connected { get; }
    public bool OutputEnabled { get; }
    public Keithley6221Settings? Settings { get; }
    public DateTimeOffset Timestamp { get; }
}

namespace InstrumentControlWorkbench.Core.Instruments.Ports;

public interface IInstrumentPort
{
    InstrumentId Id { get; }
    string DisplayName { get; }
    Devices.DeviceDescriptor Descriptor { get; }
    Task<SourceMeterSnapshot> ConnectAsync(CancellationToken cancellationToken);
    Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken cancellationToken);
    Task SafeStopAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

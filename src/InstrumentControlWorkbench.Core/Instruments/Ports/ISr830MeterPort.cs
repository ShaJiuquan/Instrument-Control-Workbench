using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments.Ports;

// Passive lock-in input only. AbortAcquisition does not disable the oscillator.
public interface ISr830MeterPort
{
    DeviceDescriptor Descriptor { get; }
    Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token);
    Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token);
    Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token);
    Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token);
    Task AbortAcquisitionAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
}
public sealed record Sr830MeterSnapshot
{
    public Sr830MeterSnapshot(bool connected, bool acquiring, Sr830InputSettings? settings, DateTimeOffset timestamp)
    {
        if (connected && settings is null || acquiring && !connected) throw new ArgumentException("Connected lock-in requires observed input settings; acquisition requires connection.");
        Connected = connected; Acquiring = acquiring; Settings = settings; Timestamp = timestamp;
    }
    public bool Connected { get; }
    public bool Acquiring { get; }
    public Sr830InputSettings? Settings { get; }
    public DateTimeOffset Timestamp { get; }
}
// One SNAP response; X/Y and R/theta are pair-coherent, not mathematically
// identical instants. Keep the reported magnitude/phase; never reconstruct them.
public sealed record Sr830RawFrame
{
    public Sr830RawFrame(Quantity x, Quantity y, Quantity r, Quantity theta, DateTimeOffset capturedAt)
    {
        Sr830Parameters.X.Validate(x); Sr830Parameters.Y.Validate(y); Sr830Parameters.R.Validate(r); Sr830Parameters.Theta.Validate(theta);
        X = x; Y = y; R = r; Theta = theta; CapturedAt = capturedAt;
    }
    public Quantity X { get; }
    public Quantity Y { get; }
    public Quantity R { get; }
    public Quantity Theta { get; }
    public DateTimeOffset CapturedAt { get; }
}
public sealed record Sr830FrameObservation(Sr830RawFrame Frame, Sr830InputSettings Settings);

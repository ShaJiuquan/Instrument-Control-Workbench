using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

// Fixed raw-voltage simulation, independent of any DC source or sample.
public sealed class MockSr830Meter : ISr830MeterPort
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool connected;
    private Sr830InputSettings settings = new(Sr830InputMode.VoltageAB, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12);
    public MockSr830Meter(InstrumentId id, string displayName) => Descriptor = new(id, "SR830", displayName, DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Sr830Parameters.WithReference);
    private readonly Sr830ReferenceReadback reference = new(Sr830ReferenceSource.Internal, new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt), 1);
    public DeviceDescriptor Descriptor { get; }
    public async Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { connected = true; return Snapshot(); } finally { gate.Release(); }
    }
    public async Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { return Snapshot(); } finally { gate.Release(); }
    }
    public async Task ConfigureAsync(Sr830InputSettings configuration, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration); await gate.WaitAsync(token);
        try { RequireConnected(); settings = configuration; } finally { gate.Release(); }
    }
    public async Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireConnected(); return new(new(.001, PhysicalUnit.Volt), new(-.002, PhysicalUnit.Volt), new(.0023, PhysicalUnit.Volt), new(-63, PhysicalUnit.Degree), DateTimeOffset.UtcNow); }
        finally { gate.Release(); }
    }
    public async Task AbortAcquisitionAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { token.ThrowIfCancellationRequested(); } finally { gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { connected = false; } finally { gate.Release(); }
    }
    private Sr830MeterSnapshot Snapshot() => new(connected, false, connected ? settings : null, DateTimeOffset.UtcNow, connected ? reference : null);
    private void RequireConnected() { if (!connected) throw new InvalidOperationException($"{Descriptor.Id} is disconnected."); }
}

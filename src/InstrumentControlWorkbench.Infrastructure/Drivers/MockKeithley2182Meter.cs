using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

// Explicit simulation: fixed input voltages, no electrical coupling to a source,
// noise, filter timing, trigger buffer, or hardware quality certification.
public sealed class MockKeithley2182Meter : IK2182MeterPort
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool connected;
    private Keithley2182Settings settings = new(1, null, 1, false, 10, AveragingMode.Repeat, 0.1);
    public MockKeithley2182Meter(InstrumentId id, string displayName)
        => Descriptor = new(id, "K2182", displayName, DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Keithley2182Parameters.All);
    public DeviceDescriptor Descriptor { get; }
    public async Task<K2182MeterSnapshot> ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { connected = true; return new(connected, false, connected ? settings : null, DateTimeOffset.UtcNow); }
        finally { gate.Release(); }
    }
    public async Task<K2182MeterSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return new(connected, false, connected ? settings : null, DateTimeOffset.UtcNow); }
        finally { gate.Release(); }
    }
    public async Task ConfigureAsync(Keithley2182Settings configuration, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await gate.WaitAsync(token);
        try { if (!connected) throw new InvalidOperationException($"{Descriptor.Id} is disconnected."); settings = configuration; }
        finally { gate.Release(); }
    }
    public async Task<K2182VoltageReading> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { if (!connected) throw new InvalidOperationException($"{Descriptor.Id} is disconnected."); return new(new(settings.Channel * 0.001, PhysicalUnit.Volt), DateTimeOffset.UtcNow); }
        finally { gate.Release(); }
    }
    public async Task AbortAcquisitionAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { connected = false; }
        finally { gate.Release(); }
    }
}

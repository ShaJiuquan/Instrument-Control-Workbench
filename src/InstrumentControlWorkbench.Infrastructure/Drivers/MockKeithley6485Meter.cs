using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

// Fixed 1 nA simulation; no source coupling, filter timing or calibration model.
public sealed class MockKeithley6485Meter : IK6485MeterPort
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool connected;
    private Keithley6485Settings settings = new(null, 1, true, false, false, 10, AveragingMode.Repeat);
    public MockKeithley6485Meter(InstrumentId id, string displayName) => Descriptor = new(id, "K6485", displayName, DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Keithley6485Parameters.All);
    public DeviceDescriptor Descriptor { get; }
    public async Task<K6485MeterSnapshot> ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { connected = true; return Snapshot(); } finally { gate.Release(); }
    }
    public async Task<K6485MeterSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Snapshot(); } finally { gate.Release(); }
    }
    private K6485MeterSnapshot Snapshot() => new(connected, false, connected ? settings : null, DateTimeOffset.UtcNow);
    public async Task ConfigureAsync(Keithley6485Settings configuration, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration); await gate.WaitAsync(token);
        try { RequireConnected(); settings = configuration; } finally { gate.Release(); }
    }
    public async Task<K6485CurrentReading> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireConnected(); if (settings.ZeroCheck) throw new InvalidOperationException("Zero check shorts the measurement input.");
            return new(new(1e-9, PhysicalUnit.Ampere), DateTimeOffset.UtcNow);
        }
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
    private void RequireConnected() { if (!connected) throw new InvalidOperationException($"{Descriptor.Id} is disconnected."); }
}

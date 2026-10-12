using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

public sealed class MockKeithley6221CurrentSource : IK6221CurrentSourcePort
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool connected; private bool output;
    private Keithley6221Settings settings = new(new(0, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt));
    public MockKeithley6221CurrentSource(InstrumentId id, string displayName)
        => Descriptor = new(id, "K6221", displayName, DriverKind.Simulation, DeviceStopPolicy.OutputOff, Keithley6221Parameters.All);
    public DeviceDescriptor Descriptor { get; }
    public async Task<K6221CurrentSourceSnapshot> ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { connected = true; return Snapshot(); } finally { gate.Release(); }
    }
    public async Task<K6221CurrentSourceSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { return Snapshot(); } finally { gate.Release(); }
    }
    public async Task ConfigureAsync(Keithley6221Settings requested, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(requested); await gate.WaitAsync(token);
        try { if (!connected) throw new InvalidOperationException("K6221 simulator is disconnected."); settings = new(requested.Current, requested.Compliance, requested.ManualRange, requested.SourceDelay ?? settings.SourceDelay); } finally { gate.Release(); }
    }
    public async Task SetOutputAsync(bool enabled, CancellationToken token)
    {
        await gate.WaitAsync(token); try { if (!connected) throw new InvalidOperationException("K6221 simulator is disconnected."); output = enabled; } finally { gate.Release(); }
    }
    private K6221CurrentSourceSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new(connected, output, connected ? settings : null, now, connected ? new(false, false, false, false, false, 1024, now) : null);
    }
    public Task SafeStopAsync(CancellationToken token) => SetOutputAsync(false, token);
    public async Task DisconnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token); try { if (output) throw new InvalidOperationException("K6221 simulator requires output-off before disconnect."); connected = false; } finally { gate.Release(); }
    }
}

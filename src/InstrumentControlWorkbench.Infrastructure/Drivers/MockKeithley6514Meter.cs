using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

// Fixed per-function simulation; no electrical coupling, charge integration or calibration model.
public sealed class MockKeithley6514Meter : IK6514MeterPort
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool connected;
    private Keithley6514Settings settings = new(ElectrometerFunction.Current, new(new(2e-4, PhysicalUnit.Ampere), true), new(new(20, PhysicalUnit.Volt), true), new(new(2e5, PhysicalUnit.Ohm), true), new(new(2e-6, PhysicalUnit.Coulomb), true), 1, true, false, false, 10, AveragingMode.Repeat);
    public MockKeithley6514Meter(InstrumentId id, string displayName) => Descriptor = new(id, "K6514", displayName, DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Keithley6514Parameters.All);
    public DeviceDescriptor Descriptor { get; }
    public async Task<K6514MeterSnapshot> ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { connected = true; return Snapshot(); } finally { gate.Release(); }
    }
    public async Task<K6514MeterSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return Snapshot(); } finally { gate.Release(); }
    }
    private K6514MeterSnapshot Snapshot() => new(connected, false, connected ? settings : null, DateTimeOffset.UtcNow);
    public async Task SelectFunctionAsync(ElectrometerFunction function, CancellationToken token)
    {
        await gate.WaitAsync(token); try { RequireConnected(); settings = settings.SelectFunction(function); } finally { gate.Release(); }
    }
    public async Task ConfigureAsync(Keithley6514Settings configuration, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(configuration); await gate.WaitAsync(token);
        try { RequireConnected(); settings = configuration; } finally { gate.Release(); }
    }
    public async Task<K6514Reading> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireConnected(); if (settings.ZeroCheck) throw new InvalidOperationException("Zero check shorts the measurement input.");
            var value = settings.Function switch { ElectrometerFunction.Current => 1e-12, ElectrometerFunction.Voltage => 0.001, ElectrometerFunction.Resistance => 1000, _ => 1e-9 };
            return new(settings.Function, new(value, Keithley6514Settings.Unit(settings.Function)), DateTimeOffset.UtcNow);
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

using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Transport;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

// Native 2450 SCPI only. Constructors never open a socket. Configuration snapshots
// are freshly reconciled over the wire; initial/last-known fields do not prove IO.
public sealed partial class TcpKeithley2450SourceMeter : ISourceMeterPort
{
    private readonly TcpScpiTransport transport;
    private readonly SemaphoreSlim operations = new(1, 1);
    private SourceMeterSnapshot snapshot;
    public TcpKeithley2450SourceMeter(InstrumentId id, string displayName, TcpDeviceConnection connection)
    {
        Id = id; DisplayName = displayName;
        Descriptor = new(id, "K2450", displayName, DriverKind.Hardware, DeviceStopPolicy.OutputOff,
            [Keithley2450Parameters.SourceVoltage, Keithley2450Parameters.SourceCurrent, Keithley2450Parameters.CurrentCompliance,
             Keithley2450Parameters.VoltageCompliance, Keithley2450Parameters.MeasuredVoltage, Keithley2450Parameters.MeasuredCurrent,
             Keithley2450Parameters.MeasuredResistance, Keithley2450Parameters.Nplc, Keithley2450Parameters.ManualVoltageRange,
             Keithley2450Parameters.ManualCurrentRange, Keithley2450Parameters.SourceAutoRange, Keithley2450Parameters.EnableSourceAutoRange,
             Keithley2450Parameters.ManualSenseVoltageRange, Keithley2450Parameters.ManualSenseCurrentRange, Keithley2450Parameters.ManualSenseResistanceRange,
             Keithley2450Parameters.SenseAutoRange, Keithley2450Parameters.EnableSenseAutoRange]);
        transport = new(connection);
        snapshot = new(id, "Not connected", InstrumentLifecycleState.Disconnected, false, false, Terminals.Front,
            new(SourceFunction.Voltage, 0, 105e-6), new(SenseFunction.Current, SensingMode.TwoWire, 1, true), DateTimeOffset.MinValue);
    }
    public InstrumentId Id { get; }
    public string DisplayName { get; }
    public DeviceDescriptor Descriptor { get; }
    public async Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            if (!transport.IsConnected)
            {
                await transport.OpenAsync(token);
                try
                {
                    var identifier = await transport.QueryAsync("*IDN?", token);
                    var parts = identifier.Split(',');
                    if (identifier.Length > 1024 || parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace) ||
                        parts[0].Trim().ToUpperInvariant() is not ("KEITHLEY" or "KEITHLEY INSTRUMENTS" or "KEITHLEY INSTRUMENTS INC.") ||
                        !parts[1].Trim().Equals("MODEL 2450", StringComparison.OrdinalIgnoreCase) && !parts[1].Trim().Equals("2450", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("Endpoint identity is not a Keithley 2450.");
                    if (!(await transport.QueryAsync("*LANG?", token)).Trim().Equals("SCPI", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("Select native SCPI on the 2450; TSP and SCPI2400 are unsupported. The driver never changes language or resets the instrument.");
                    snapshot = snapshot with { Identifier = identifier };
                    return await ReconcileAsync(token);
                }
                catch { await transport.CloseAsync(CancellationToken.None); throw; }
            }
            return await ReconcileAsync(token);
        }
        finally { operations.Release(); }
    }
    public async Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try { return transport.IsConnected ? await ReconcileAsync(token) : snapshot with { Connected = false, Lifecycle = InstrumentLifecycleState.Disconnected }; }
        finally { operations.Release(); }
    }
    public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => GetSnapshotAsync(token);
    public async Task SafeStopAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            RequireConnected();
            await transport.WriteAsync(Abort, token);
            await transport.WriteAsync(OutputOff, token);
            await CheckErrorsAsync(token);
            if (Boolean(await transport.QueryAsync(OutputQuery, token))) throw new IOException("2450 output-off command was not confirmed by a fresh readback.");
            snapshot = snapshot with { OutputEnabled = false };
        }
        finally { operations.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            var state = await ReconcileAsync(token);
            if (state.OutputEnabled) throw new InvalidOperationException("Confirm 2450 output off before disconnecting.");
            await transport.CloseAsync(token); snapshot = state with { Connected = false, Lifecycle = InstrumentLifecycleState.Disconnected };
        }
        finally { operations.Release(); }
    }
    private void RequireConnected() { if (!transport.IsConnected) throw new IOException("2450 TCP is disconnected; explicit reconnect is required before fresh stop confirmation."); }
    private const string Abort = ":ABOR";
    private const string OutputOff = ":OUTP OFF";
    private const string OutputQuery = ":OUTP?";
    private const string ErrorQuery = ":SYST:ERR?";
}

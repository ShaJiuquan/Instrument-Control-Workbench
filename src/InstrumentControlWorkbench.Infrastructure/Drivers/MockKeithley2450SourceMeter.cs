using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

public sealed class MockKeithley2450SourceMeter : ISourceMeterPort
{
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private SourceSetpoint source = new(SourceFunction.Voltage, 0.0, 105e-6);
    private SenseConfiguration sense = new(SenseFunction.Current, SensingMode.FourWire, 1.0, true);
    private bool connected;
    private bool outputEnabled;
    private DateTimeOffset lastHeartbeat = DateTimeOffset.MinValue;

    public MockKeithley2450SourceMeter(InstrumentId? id = null, string? displayName = null)
    {
        DisplayName = displayName ?? "Keithley 2450 SourceMeter";
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        Id = id ?? new("k2450");
        Descriptor = new(Id, "K2450", DisplayName, DriverKind.Simulation, DeviceStopPolicy.OutputOff,
            [Keithley2450Parameters.SourceVoltage, Keithley2450Parameters.SourceCurrent,
             Keithley2450Parameters.CurrentCompliance, Keithley2450Parameters.VoltageCompliance,
             Keithley2450Parameters.MeasuredCurrent, Keithley2450Parameters.MeasuredVoltage,
             Keithley2450Parameters.MeasuredResistance, Keithley2450Parameters.Nplc, Keithley2450Parameters.ManualVoltageRange,
             Keithley2450Parameters.ManualCurrentRange, Keithley2450Parameters.SourceAutoRange, Keithley2450Parameters.EnableSourceAutoRange,
             Keithley2450Parameters.ManualSenseVoltageRange, Keithley2450Parameters.ManualSenseCurrentRange, Keithley2450Parameters.ManualSenseResistanceRange,
             Keithley2450Parameters.SenseAutoRange, Keithley2450Parameters.EnableSenseAutoRange]);
    }

    public InstrumentId Id { get; }
    public string DisplayName { get; }
    public DeviceDescriptor Descriptor { get; }

    public async Task<SourceMeterSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(80, cancellationToken);
            connected = true;
            lastHeartbeat = DateTimeOffset.Now;
            return Snapshot(InstrumentLifecycleState.Idle);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task ConfigureSourceAsync(SourceSetpoint setpoint, CancellationToken cancellationToken)
    {
        Keithley2450Limits.Validate(setpoint);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            RequireConnected();
            await Task.Delay(25, cancellationToken);
            if (outputEnabled && (source.Function != setpoint.Function || source.ManualRange != setpoint.ManualRange))
                throw new InvalidOperationException("Turn output off before changing source function or range mode.");
            Keithley2450Limits.ValidateConfiguration(setpoint, sense);
            source = setpoint;
            lastHeartbeat = DateTimeOffset.Now;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task ConfigureSenseAsync(SenseConfiguration configuration, CancellationToken cancellationToken)
    {
        Keithley2450Limits.Validate(configuration);
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            RequireConnected();
            await Task.Delay(20, cancellationToken);
            if (!configuration.AutoRange && configuration.ManualRange is null)
                configuration = configuration with { ManualRange = sense.Function == configuration.Function && sense.ManualRange is { } oldRange ? oldRange : Keithley2450SenseRanges.Resolve(configuration.Function, new(source.Compliance / 1.05, Keithley2450SenseRanges.For(configuration.Function)[0].Unit)) };
            Keithley2450Limits.ValidateConfiguration(source, configuration);
            sense = configuration;
            lastHeartbeat = DateTimeOffset.Now;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task EnableOutputAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            RequireConnected();
            await Task.Delay(20, cancellationToken);
            outputEnabled = true;
            lastHeartbeat = DateTimeOffset.Now;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task DisableOutputAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(15, cancellationToken);
            outputEnabled = false;
            lastHeartbeat = DateTimeOffset.Now;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<MeasurementValue> ReadAsync(CancellationToken cancellationToken)
    {
        var reading = await ReadSourceSenseAsync(cancellationToken);
        var unit = reading.Sense.Unit switch { PhysicalUnit.Ampere => "A", PhysicalUnit.Volt => "V", _ => "ohm" };
        return new(reading.Sense.Value, unit, reading.Timestamp);
    }

    public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            RequireConnected();
            await Task.Delay(35, cancellationToken);
            lastHeartbeat = DateTimeOffset.Now;
            var sourceUnit = source.Function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere;
            var value = sense.Function switch
            {
                SenseFunction.Current => source.Function == SourceFunction.Voltage ? MockCurrentFromVoltage(source.Level) : source.Level * 0.998,
                SenseFunction.Voltage => (source.Function == SourceFunction.Voltage ? source.Level : source.Level / 2.2e-3) * 0.998,
                SenseFunction.Resistance => 1_000.0 + Math.Abs(source.Level) * 12.0,
                _ => throw new InvalidOperationException("Unknown sensing function."),
            };
            var unit = sense.Function switch { SenseFunction.Current => PhysicalUnit.Ampere, SenseFunction.Voltage => PhysicalUnit.Volt, _ => PhysicalUnit.Ohm };
            return new(source.Function, sense.Function, new(outputEnabled ? source.Level : 0, sourceUnit),
                new(outputEnabled ? value : 0, unit), lastHeartbeat);
        }
        finally { operationLock.Release(); }
    }

    public async Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            return Snapshot(connected ? InstrumentLifecycleState.Idle : InstrumentLifecycleState.Disconnected);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (connected)
            {
                lastHeartbeat = DateTimeOffset.Now;
            }

            return Snapshot(connected ? InstrumentLifecycleState.Idle : InstrumentLifecycleState.Disconnected);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public Task SafeStopAsync(CancellationToken cancellationToken)
    {
        return DisableOutputAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (outputEnabled) throw new InvalidOperationException("Disable output before disconnecting the source meter.");
            connected = false;
            lastHeartbeat = DateTimeOffset.Now;
        }
        finally { operationLock.Release(); }
    }

    private SourceMeterSnapshot Snapshot(InstrumentLifecycleState lifecycle)
    {
        return new SourceMeterSnapshot(
            Id,
            "KEITHLEY INSTRUMENTS INC.,MODEL 2450,MOCK2450,B01",
            lifecycle,
            connected,
            outputEnabled,
            Terminals.Rear,
            source,
            sense,
            lastHeartbeat);
    }

    private static double MockCurrentFromVoltage(double voltage)
    {
        const double conductance = 2.2e-3;
        var nonlinear = Math.Tanh(voltage * 2.5) * 18e-6;
        var deterministicNoise = Math.Sin(voltage * 31.0) * 1.5e-6;
        return voltage * conductance + nonlinear + deterministicNoise;
    }

    private void RequireConnected()
    {
        if (!connected)
        {
            throw new InvalidOperationException("K2450 mock is not connected.");
        }
    }
}

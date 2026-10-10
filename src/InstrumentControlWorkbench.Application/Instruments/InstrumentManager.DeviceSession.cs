using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Instruments;

public sealed partial class InstrumentManager
{
    public async Task<DeviceStatus> ConnectDeviceAsync(CancellationToken token, RunId? owner = null)
        => ToDeviceStatus(await ConnectAsync(token, owner));

    public async Task<DeviceStatus> GetDeviceStatusAsync(CancellationToken token, RunId? owner = null)
    {
        await EnterOperationAsync(token, owner);
        try { return ToDeviceStatus(await ReadSourceSnapshotAsync(token)); }
        finally { operations.Exit(); }
    }

    public async Task DisconnectDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await EnterOperationAsync(token, owner);
        try
        {
            var snapshot = await ReadSourceSnapshotAsync(token);
            if (snapshot.OutputEnabled) throw new InvalidOperationException("Confirm output off before disconnecting the source meter.");
            await sourceMeter.DisconnectAsync(token);
            Transition(InstrumentLifecycleEvent.Disconnected);
            await PublishSnapshotAsync((await ReadSourceSnapshotAsync(token)) with { Lifecycle = lifecycle }, token);
        }
        finally { operations.Exit(); }
    }

    public async Task<DeviceStatus> StopDeviceAsync(CancellationToken token, RunId? owner = null)
    {
        await SafeStopAsync("device resource cleanup", token, owner);
        var status = await GetDeviceStatusAsync(token, owner);
        if (!status.SafetyConfirmed) throw new InvalidOperationException($"{InstrumentId} has no connected output-off confirmation.");
        return status;
    }

    public async Task<Quantity> ReadParameterAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
    {
        var descriptor = Descriptor.GetParameter(parameter);
        if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException($"{parameter} is write-only.");
        await EnterOperationAsync(token, owner);
        try
        {
            var snapshot = await ReadSourceSnapshotAsync(token);
            if (!snapshot.Connected) throw new InvalidOperationException($"{InstrumentId} is disconnected.");
            var configuration = ToDeviceStatus(snapshot);
            if (configuration.Values.TryGetValue(parameter, out var configured)) return configured;
            var sense = parameter == Keithley2450Parameters.MeasuredCurrent.Id ? SenseFunction.Current :
                parameter == Keithley2450Parameters.MeasuredVoltage.Id ? SenseFunction.Voltage :
                parameter == Keithley2450Parameters.MeasuredResistance.Id ? SenseFunction.Resistance : (SenseFunction?)null;
            if (sense is null) throw new InvalidOperationException($"{parameter} is not available in {snapshot.Source.Function} source mode.");
            try
            {
                await sourceMeter.ConfigureSenseAsync(snapshot.Sense with { Function = sense.Value }, token);
                var measured = await sourceMeter.ReadAsync(token);
                var expectedUnit = sense switch { SenseFunction.Current => "A", SenseFunction.Voltage => "V", _ => "ohm" };
                if (measured.Unit != expectedUnit) throw new InvalidOperationException($"{parameter} readback must use {expectedUnit}.");
                var value = new Quantity(measured.Value, descriptor.Unit);
                descriptor.Validate(value);
                await PublishSnapshotAsync((await ReadSourceSnapshotAsync(token)) with { Lifecycle = lifecycle }, token);
                return value;
            }
            catch { Transition(InstrumentLifecycleEvent.FaultDetected); throw; }
        }
        finally { operations.Exit(); }
    }

    public async Task<SourceSenseReading> ReadSourceSenseAsync(SourceFunction function, CancellationToken token, RunId? owner = null, SensingMode? requiredSensingMode = null)
    {
        if (requiredSensingMode is { } required && !Enum.IsDefined(required)) throw new ArgumentOutOfRangeException(nameof(requiredSensingMode));
        await EnterOperationAsync(token, owner);
        try
        {
            var snapshot = await ReadSourceSnapshotAsync(token);
            if (!snapshot.Connected || !snapshot.OutputEnabled) throw new InvalidOperationException("Source/sense acquisition requires connected source output.");
            if (snapshot.Source.Function != function) throw new InvalidOperationException("Source function differs from the acquisition plan.");
            if (requiredSensingMode is { } mode && snapshot.Sense.Mode != mode) throw new InvalidOperationException("Actual sensing mode differs from the required preparation or sample topology.");
            var senseFunction = function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage;
            try
            {
                if (snapshot.Sense.Function != senseFunction)
                    await sourceMeter.ConfigureSenseAsync(snapshot.Sense with { Function = senseFunction }, token);
                var reading = await sourceMeter.ReadSourceSenseAsync(token);
                if (reading.Function != function || reading.SenseFunction != senseFunction)
                    throw new InvalidOperationException("Source/sense readback functions differ from the requested acquisition.");
                var after = await ReadSourceSnapshotAsync(token);
                if (!after.Connected || !after.OutputEnabled || after.Source != snapshot.Source || after.Sense != (snapshot.Sense with { Function = senseFunction }) || after.Terminals != snapshot.Terminals ||
                    requiredSensingMode is { } expectedMode && after.Sense.Mode != expectedMode)
                    throw new InvalidOperationException("Source state or sensing configuration changed during acquisition.");
                await PublishSnapshotAsync(after with { Lifecycle = lifecycle }, token);
                return reading;
            }
            catch { Transition(InstrumentLifecycleEvent.FaultDetected); throw; }
        }
        finally { operations.Exit(); }
    }

    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Descriptor.GetParameter(parameter).ValidateWrite(value);
        await EnterOperationAsync(token, owner);
        try
        {
            var snapshot = await ReadSourceSnapshotAsync(token);
            if (!snapshot.Connected) throw new InvalidOperationException($"{InstrumentId} is disconnected.");
            var voltage = snapshot.Source.Function == SourceFunction.Voltage;
            var levelId = voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id;
            var complianceId = voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id;
            if (parameter != levelId && parameter != complianceId && parameter != Keithley2450Parameters.Nplc.Id)
                throw new InvalidOperationException($"{parameter} requires a different source mode; configure source mode and compliance explicitly first.");
            var source = parameter == levelId ? snapshot.Source with { Level = value.Value } :
                parameter == complianceId ? snapshot.Source with { Compliance = value.Value } : snapshot.Source;
            Keithley2450Limits.Validate(source);
            try
            {
                if (parameter == levelId || parameter == complianceId) await sourceMeter.ConfigureSourceAsync(source, token);
                else if (parameter == Keithley2450Parameters.Nplc.Id) await sourceMeter.ConfigureSenseAsync(snapshot.Sense with { Nplc = value.Value }, token);
                await PublishSnapshotAsync((await ReadSourceSnapshotAsync(token)) with { Lifecycle = lifecycle }, token);
            }
            catch { Transition(InstrumentLifecycleEvent.FaultDetected); throw; }
        }
        finally { operations.Exit(); }
    }

    private async Task<SourceMeterSnapshot> ReadSourceSnapshotAsync(CancellationToken token)
    {
        try { return await sourceMeter.GetSnapshotAsync(token); }
        catch { Transition(InstrumentLifecycleEvent.FaultDetected); throw; }
    }

    internal async Task<SourceMeterSnapshot> CaptureRunSourceStateAsync(RunId owner, CancellationToken token)
    {
        await EnterOperationAsync(token, owner);
        try { return await ReadSourceSnapshotAsync(token); }
        finally { operations.Exit(); }
    }

    private DeviceStatus ToDeviceStatus(SourceMeterSnapshot snapshot)
    {
        var voltage = snapshot.Source.Function == SourceFunction.Voltage;
        var level = voltage ? Keithley2450Parameters.SourceVoltage : Keithley2450Parameters.SourceCurrent;
        var compliance = voltage ? Keithley2450Parameters.CurrentCompliance : Keithley2450Parameters.VoltageCompliance;
        return new(Descriptor, lifecycle, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.OutputEnabled,
            snapshot.LastHeartbeat, !snapshot.Connected && Descriptor.DriverKind == DriverKind.Hardware ? new Dictionary<ParameterId, Quantity>() : new Dictionary<ParameterId, Quantity>
            {
                [level.Id] = new(snapshot.Source.Level, level.Unit),
                [compliance.Id] = new(snapshot.Source.Compliance, compliance.Unit),
                [Keithley2450Parameters.Nplc.Id] = new(snapshot.Sense.Nplc, PhysicalUnit.Dimensionless),
            });
    }
}

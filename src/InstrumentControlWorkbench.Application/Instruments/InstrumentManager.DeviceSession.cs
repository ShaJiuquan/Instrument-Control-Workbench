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
        => (await ReadObservationAsync(parameter, token, owner)).Value;

    public async Task<ParameterObservation> ReadObservationAsync(ParameterId parameter, CancellationToken token, RunId? owner = null)
    {
        var descriptor = Descriptor.GetParameter(parameter);
        if (!descriptor.Access.HasFlag(ParameterAccess.Read)) throw new InvalidOperationException($"{parameter} is write-only.");
        await EnterOperationAsync(token, owner);
        try
        {
            var snapshot = await ReadSourceSnapshotAsync(token);
            if (!snapshot.Connected) throw new InvalidOperationException($"{InstrumentId} is disconnected.");
            var configuration = ToDeviceStatus(snapshot);
            if (configuration.Values.TryGetValue(parameter, out var configured)) return new(configured, snapshot.LastHeartbeat);
            if (parameter == Keithley2450Parameters.ManualVoltageRange.Id || parameter == Keithley2450Parameters.ManualCurrentRange.Id || parameter == Keithley2450Parameters.ManualSenseVoltageRange.Id || parameter == Keithley2450Parameters.ManualSenseCurrentRange.Id || parameter == Keithley2450Parameters.ManualSenseResistanceRange.Id)
                throw new InvalidOperationException("Manual source/sense range is unavailable in the current function or automatic range mode.");
            var sense = parameter == Keithley2450Parameters.MeasuredCurrent.Id ? SenseFunction.Current :
                parameter == Keithley2450Parameters.MeasuredVoltage.Id ? SenseFunction.Voltage :
                parameter == Keithley2450Parameters.MeasuredResistance.Id ? SenseFunction.Resistance : (SenseFunction?)null;
            if (sense is null) throw new InvalidOperationException($"{parameter} is not available in {snapshot.Source.Function} source mode.");
            try
            {
                if (snapshot.Sense.ManualRange is not null && snapshot.Sense.Function != sense.Value)
                    throw new InvalidOperationException("Configure the requested sense function and its range explicitly before acquisition.");
                var requestedSense = snapshot.Sense with { Function = sense.Value, Acquisition = snapshot.Sense.Function == sense.Value ? snapshot.Sense.Acquisition : null };
                if (requestedSense != snapshot.Sense)
                {
                    await sourceMeter.ConfigureSenseAsync(requestedSense, token);
                    var preparedSnapshot = await ReadSourceSnapshotAsync(token);
                    requestedSense = requestedSense with { Acquisition = preparedSnapshot.Sense.Acquisition };
                    if (!preparedSnapshot.Connected || preparedSnapshot.Source != snapshot.Source || preparedSnapshot.Sense != requestedSense || preparedSnapshot.OutputEnabled != snapshot.OutputEnabled || preparedSnapshot.Terminals != snapshot.Terminals) throw new InvalidOperationException("Sense function preparation changed unexpected state.");
                }
                var measured = await sourceMeter.ReadAsync(token);
                var expectedUnit = sense switch { SenseFunction.Current => "A", SenseFunction.Voltage => "V", _ => "ohm" };
                if (measured.Unit != expectedUnit) throw new InvalidOperationException($"{parameter} readback must use {expectedUnit}.");
                var value = new Quantity(measured.Value, descriptor.Unit);
                descriptor.Validate(value);
                var after = await ReadSourceSnapshotAsync(token);
                if (!after.Connected || after.Source != snapshot.Source || after.Sense != requestedSense || after.OutputEnabled != snapshot.OutputEnabled || after.Terminals != snapshot.Terminals)
                    throw new InvalidOperationException("Source state or sensing configuration changed during acquisition.");
                measured.Status?.ValidateAcquisition(requestedSense.Mode, snapshot.Terminals, requireSourceReadback: false);
                await PublishSnapshotAsync(after with { Lifecycle = lifecycle }, token);
                return new(value, measured.Timestamp, measured.Status, requestedSense.Acquisition);
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
                if (snapshot.Sense.ManualRange is not null && snapshot.Sense.Function != senseFunction)
                    throw new InvalidOperationException("Configure complementary sensing and its range explicitly before paired acquisition.");
                var expectedSense = snapshot.Sense;
                if (snapshot.Sense.Function != senseFunction)
                {
                    expectedSense = snapshot.Sense with { Function = senseFunction, Acquisition = null };
                    await sourceMeter.ConfigureSenseAsync(expectedSense, token);
                    var preparedSnapshot = await ReadSourceSnapshotAsync(token);
                    expectedSense = expectedSense with { Acquisition = preparedSnapshot.Sense.Acquisition };
                    if (!preparedSnapshot.Connected || preparedSnapshot.Source != snapshot.Source || preparedSnapshot.Sense != expectedSense || preparedSnapshot.OutputEnabled != snapshot.OutputEnabled || preparedSnapshot.Terminals != snapshot.Terminals) throw new InvalidOperationException("Complementary sensing changed unexpected state.");
                }
                var reading = await sourceMeter.ReadSourceSenseAsync(token);
                if (reading.Function != function || reading.SenseFunction != senseFunction)
                    throw new InvalidOperationException("Source/sense readback functions differ from the requested acquisition.");
                var after = await ReadSourceSnapshotAsync(token);
                if (!after.Connected || !after.OutputEnabled || after.Source != snapshot.Source || after.Sense != expectedSense || after.Terminals != snapshot.Terminals ||
                    requiredSensingMode is { } expectedMode && after.Sense.Mode != expectedMode)
                    throw new InvalidOperationException("Source state or sensing configuration changed during acquisition.");
                reading.Status?.ValidateAcquisition(snapshot.Sense.Mode, snapshot.Terminals, requireSourceReadback: true);
                await PublishSnapshotAsync(after with { Lifecycle = lifecycle }, token);
                return new(reading.Function, reading.SenseFunction, reading.Source, reading.Sense, reading.Timestamp, reading.Status, expectedSense.Acquisition);
            }
            catch { Transition(InstrumentLifecycleEvent.FaultDetected); throw; }
        }
        finally { operations.Exit(); }
    }

    public async Task WriteParameterAsync(ParameterId parameter, Quantity value, CancellationToken token, RunId? owner = null)
    {
        Descriptor.GetParameter(parameter).ValidateWrite(value);
        if (Keithley2450AcquisitionSettings.IsParameter(parameter)) Keithley2450AcquisitionSettings.ValidateParameter(parameter, value);
        if (parameter == Keithley2450Parameters.ActiveSenseFunction.Id && value.Value != Math.Truncate(value.Value)) throw new ArgumentException("Sense function requires an integral enum code.");
        await EnterOperationAsync(token, owner);
        try
        {
            var snapshot = await ReadSourceSnapshotAsync(token);
            if (!snapshot.Connected) throw new InvalidOperationException($"{InstrumentId} is disconnected.");
            if (parameter == Keithley2450Parameters.RefreshZero.Id)
            {
                if (snapshot.OutputEnabled) throw new InvalidOperationException("Turn output off before refreshing zero/reference.");
                try
                {
                    await sourceMeter.RefreshZeroAsync(token);
                    var after = await ReadSourceSnapshotAsync(token);
                    if (!after.Connected || after.OutputEnabled || after.Source != snapshot.Source || after.Sense != snapshot.Sense || after.Terminals != snapshot.Terminals) throw new InvalidOperationException("Zero/reference refresh changed unexpected state.");
                    await PublishSnapshotAsync(after with { Lifecycle = lifecycle }, token);
                    await PublishLogAsync("Zero/reference refresh completed for the active aperture.", token);
                    return;
                }
                catch { Transition(InstrumentLifecycleEvent.FaultDetected); throw; }
            }
            var functionWrite = parameter == Keithley2450Parameters.ActiveSenseFunction.Id;
            var acquisitionWrite = Keithley2450AcquisitionSettings.IsParameter(parameter);
            if (parameter == Keithley2450Parameters.AutorangeRebound.Id && snapshot.Sense.Function == SenseFunction.Resistance) throw new NotSupportedException("Resistance sensing has no autorange rebound control.");
            var voltage = snapshot.Source.Function == SourceFunction.Voltage;
            var levelId = voltage ? Keithley2450Parameters.SourceVoltage.Id : Keithley2450Parameters.SourceCurrent.Id;
            var complianceId = voltage ? Keithley2450Parameters.CurrentCompliance.Id : Keithley2450Parameters.VoltageCompliance.Id;
            var rangeId = voltage ? Keithley2450Parameters.ManualVoltageRange.Id : Keithley2450Parameters.ManualCurrentRange.Id;
            var rangeWrite = parameter == rangeId || parameter == Keithley2450Parameters.EnableSourceAutoRange.Id;
            var senseRangeId = Keithley2450Parameters.ManualSenseRange(snapshot.Sense.Function).Id;
            var senseRangeWrite = parameter == senseRangeId || parameter == Keithley2450Parameters.EnableSenseAutoRange.Id;
            if (parameter != levelId && parameter != complianceId && parameter != Keithley2450Parameters.Nplc.Id && !rangeWrite && !senseRangeWrite && !acquisitionWrite && !functionWrite)
                throw new InvalidOperationException($"{parameter} requires a different source mode; configure source mode and compliance explicitly first.");
            if (rangeWrite && snapshot.OutputEnabled) throw new InvalidOperationException("Confirm output off before changing source range.");
            var source = parameter == levelId ? snapshot.Source with { Level = value.Value } :
                parameter == complianceId ? snapshot.Source with { Compliance = value.Value } :
                parameter == rangeId ? snapshot.Source with { ManualRange = value } :
                parameter == Keithley2450Parameters.EnableSourceAutoRange.Id ? snapshot.Source with { ManualRange = null } : snapshot.Source;
            var sense = parameter == senseRangeId ? snapshot.Sense with { AutoRange = false, ManualRange = value } :
                parameter == Keithley2450Parameters.EnableSenseAutoRange.Id ? snapshot.Sense with { AutoRange = true, ManualRange = null } : snapshot.Sense;
            if (parameter == Keithley2450Parameters.Nplc.Id) sense = sense with { Nplc = value.Value };
            if (functionWrite && (SenseFunction)(int)value.Value != snapshot.Sense.Function) sense = sense with { Function = (SenseFunction)(int)value.Value, AutoRange = true, ManualRange = null, Acquisition = null };
            if (acquisitionWrite) sense = sense with { Acquisition = (snapshot.Sense.Acquisition ?? throw new NotSupportedException("Observed acquisition settings are required before changing an individual setting.")).WithParameter(parameter, value) };
            Keithley2450Limits.ValidateConfiguration(source, sense);
            try
            {
                if (parameter == levelId || parameter == complianceId || rangeWrite) await sourceMeter.ConfigureSourceAsync(source, token);
                else if (senseRangeWrite || acquisitionWrite || functionWrite || parameter == Keithley2450Parameters.Nplc.Id) await sourceMeter.ConfigureSenseAsync(sense, token);
                var after = await ReadSourceSnapshotAsync(token);
                if (senseRangeWrite || acquisitionWrite || functionWrite || parameter == Keithley2450Parameters.Nplc.Id)
                {
                    var expectedSense = sense with { Acquisition = sense.Acquisition ?? after.Sense.Acquisition };
                    if (!after.Connected || after.Source != snapshot.Source || after.Sense != expectedSense || after.OutputEnabled != snapshot.OutputEnabled || after.Terminals != snapshot.Terminals) throw new InvalidOperationException("Sense setting changed unexpected state.");
                }
                await PublishSnapshotAsync(after with { Lifecycle = lifecycle }, token);
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
        var values = new Dictionary<ParameterId, Quantity>();
        if (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation)
        {
            values[level.Id] = new(snapshot.Source.Level, level.Unit);
            values[compliance.Id] = new(snapshot.Source.Compliance, compliance.Unit);
            values[Keithley2450Parameters.ActiveSenseFunction.Id] = new((int)snapshot.Sense.Function, PhysicalUnit.Dimensionless);
            values[Keithley2450Parameters.Nplc.Id] = new(snapshot.Sense.Nplc, PhysicalUnit.Dimensionless);
            values[Keithley2450Parameters.SenseAutoRange.Id] = new(snapshot.Sense.AutoRange ? 1 : 0, PhysicalUnit.Dimensionless);
            if (snapshot.Sense.Acquisition is { } acquisition)
                foreach (var captured in acquisition.CaptureParameters(snapshot.Sense.Function)) values[captured.Descriptor.Id] = captured.Value;
            if (snapshot.Sense.ManualRange is { } senseRange) values[Keithley2450Parameters.ManualSenseRange(snapshot.Sense.Function).Id] = senseRange;
            values[Keithley2450Parameters.SourceAutoRange.Id] = new(snapshot.Source.ManualRange is null ? 1 : 0, PhysicalUnit.Dimensionless);
            if (snapshot.Source.ManualRange is { } range) values[(voltage ? Keithley2450Parameters.ManualVoltageRange : Keithley2450Parameters.ManualCurrentRange).Id] = range;
        }
        return new(Descriptor, lifecycle, snapshot.Connected, (snapshot.Connected || Descriptor.DriverKind == DriverKind.Simulation) && !snapshot.OutputEnabled,
            snapshot.LastHeartbeat, values);
    }
}

using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    public async Task ConfigureCurrentSourceAsync(InstrumentId id, Keithley6221Settings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings); await gate.WaitAsync(token);
        try
        {
            RequireActive(); var current = Get(id) as Keithley6221CurrentSourceSession ?? throw new NotSupportedException("Device has no pure K6221 current-source profile.");
            await PrepareMutationAsync(id); touched.Add(id); await current.ConfigureAsync(settings, Owner, token);
        }
        finally { gate.Release(); }
    }
    public async Task<(IndependentResistanceEvidence Evidence, IReadOnlyList<AcquisitionQuality> Quality)> ReadCurrentResistanceAsync(
        ReadCurrentResistanceStep step, Quantity? expectedProgrammedCurrent, CancellationToken token, int? requiredMeterInput = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (expectedProgrammedCurrent is { } expected && (expected.Unit != PhysicalUnit.Ampere || Math.Abs(expected.Value) <= step.MinimumCurrent.Value))
            throw new ArgumentException("Expected programmed current must be defined Ampere excitation.");
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var source = Get(step.Source);
            var manager = source as InstrumentManager; var pure = source as Keithley6221CurrentSourceSession;
            if (manager is null && pure is null || pure is not null && step.CurrentReference != CurrentReference.ProgrammedSetpoint) throw new NotSupportedException("Current source does not support the requested acquisition reference.");
            var meter = Get(step.Meter) as Keithley2182MeterSession ?? throw new NotSupportedException("Current resistance requires an independent K2182 meter session.");
            await PrepareMutationAsync(step.Source); await PrepareMutationAsync(step.Meter);
            touched.Add(step.Source); touched.Add(step.Meter);
            if (step.RequiredRoute is { } route)
            {
                await PrepareMutationAsync(route.Device); touched.Add(route.Device);
                await ((MatrixSwitchSession)Get(route.Device)).ConfirmHeldRouteAsync(Owner, token, route.Closed);
            }
            SourceMeterSnapshot? before = manager is null ? null : await manager.CaptureRunSourceStateAsync(Owner, token);
            K6221CurrentSourceSnapshot? pureBefore = pure is null ? null : await pure.CaptureSourceStateAsync(Owner, token);
            var beforeAt = DateTimeOffset.UtcNow;
            if (before is not null && (!before.Connected || !before.OutputEnabled || before.Source.Function != SourceFunction.Current) || pureBefore is not null && !pureBefore.OutputEnabled)
                throw new InvalidOperationException("Current excitation is disconnected, off or wrong function.");
            if (before is not null) Keithley2450Limits.ValidateConfiguration(before.Source, before.Sense);
            var configuration = before is not null ? new CurrentExcitationConfiguration(before.Source) : new CurrentExcitationConfiguration(CurrentSourceModel.Keithley6221, pureBefore!.Settings!.Current, pureBefore.Settings.Compliance, pureBefore.Settings.ManualRange);
            if (expectedProgrammedCurrent is { } requested && configuration.Level != requested.Value) throw new InvalidOperationException("Current excitation differs from the expected programmed current.");
            SourceSenseReading? sourceFrame = null;
            if (step.CurrentReference == CurrentReference.MeasuredSourceReadback)
                sourceFrame = await manager!.ReadSourceSenseAsync(SourceFunction.Current, token, Owner, before!.Sense.Mode);
            var current = sourceFrame?.Source ?? new(configuration.Level, PhysicalUnit.Ampere);
            if (Math.Abs(current.Value) <= step.MinimumCurrent.Value) throw new InvalidOperationException("Current is zero or below the resistance threshold.");
            var voltage = await meter.ReadObservationAsync(Keithley2182Parameters.Voltage.Id, token, Owner, requiredMeterInput);
            token.ThrowIfCancellationRequested();
            if (manager is not null)
            {
                var after = await manager.CaptureRunSourceStateAsync(Owner, token);
                if (!after.Connected || !after.OutputEnabled || before!.Source != after.Source || before.Sense != after.Sense || before.Terminals != after.Terminals) throw new InvalidOperationException("Source state changed across independent voltage acquisition; resistance discarded.");
            }
            else
            {
                var after = await pure!.CaptureSourceStateAsync(Owner, token);
                if (!after.OutputEnabled || after.Settings != pureBefore!.Settings) throw new InvalidOperationException("Pure current-source state changed across voltage acquisition; resistance discarded.");
            }
            var afterAt = DateTimeOffset.UtcNow;
            if (step.RequiredRoute is { } expectedRoute)
                await ((MatrixSwitchSession)Get(expectedRoute.Device)).ConfirmHeldRouteAsync(Owner, token, expectedRoute.Closed);
            var currentAt = sourceFrame?.Timestamp ?? beforeAt;
            var evidence = new IndependentResistanceEvidence(step, configuration, current, voltage.Value, currentAt, voltage.CapturedAt, beforeAt, afterAt);
            IReadOnlyList<AcquisitionQuality> quality = Array.AsReadOnly(new[]
            {
                new AcquisitionQuality(step.Id, step.Source, step.CurrentColumn, currentAt, sourceFrame?.Status, sourceFrame?.AcquisitionConfiguration),
                new AcquisitionQuality(step.Id, step.Meter, step.VoltageColumn, voltage.CapturedAt, voltage.Status, voltage.AcquisitionConfiguration, voltage.MeterConfiguration),
            });
            return (evidence, quality);
        }
        finally { gate.Release(); }
    }
}

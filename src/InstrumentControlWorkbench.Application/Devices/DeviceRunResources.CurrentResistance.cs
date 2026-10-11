using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

public sealed partial class DeviceRunResources
{
    public async Task<(IndependentResistanceEvidence Evidence, IReadOnlyList<AcquisitionQuality> Quality)> ReadCurrentResistanceAsync(
        ReadCurrentResistanceStep step, Quantity? expectedProgrammedCurrent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (expectedProgrammedCurrent is { } expected && (expected.Unit != PhysicalUnit.Ampere || Math.Abs(expected.Value) <= step.MinimumCurrent.Value))
            throw new ArgumentException("Expected programmed current must be defined Ampere excitation.");
        await gate.WaitAsync(token);
        try
        {
            RequireActive();
            var source = Get(step.Source) as InstrumentManager ?? throw new NotSupportedException("Current resistance requires a supported excitation source.");
            var meter = Get(step.Meter) as Keithley2182MeterSession ?? throw new NotSupportedException("Current resistance requires an independent K2182 meter session.");
            await PrepareMutationAsync(step.Source); await PrepareMutationAsync(step.Meter);
            touched.Add(step.Source); touched.Add(step.Meter);
            if (step.RequiredRoute is { } route)
            {
                await PrepareMutationAsync(route.Device); touched.Add(route.Device);
                await ((MatrixSwitchSession)Get(route.Device)).ConfirmHeldRouteAsync(Owner, token, route.Closed);
            }
            var before = await source.CaptureRunSourceStateAsync(Owner, token); var beforeAt = DateTimeOffset.UtcNow;
            if (!before.Connected || !before.OutputEnabled || before.Source.Function != SourceFunction.Current ||
                expectedProgrammedCurrent is { } requested && before.Source.Level != requested.Value)
                throw new InvalidOperationException("Current excitation is disconnected, off, wrong function or differs from the expected programmed current.");
            Keithley2450Limits.ValidateConfiguration(before.Source, before.Sense);
            SourceSenseReading? sourceFrame = null;
            if (step.CurrentReference == CurrentReference.MeasuredSourceReadback)
                sourceFrame = await source.ReadSourceSenseAsync(SourceFunction.Current, token, Owner, before.Sense.Mode);
            var current = sourceFrame?.Source ?? new(before.Source.Level, PhysicalUnit.Ampere);
            if (Math.Abs(current.Value) <= step.MinimumCurrent.Value) throw new InvalidOperationException("Current is zero or below the resistance threshold.");
            var voltage = await meter.ReadObservationAsync(Keithley2182Parameters.Voltage.Id, token, Owner);
            token.ThrowIfCancellationRequested();
            var after = await source.CaptureRunSourceStateAsync(Owner, token); var afterAt = DateTimeOffset.UtcNow;
            if (!after.Connected || !after.OutputEnabled || before.Source != after.Source || before.Sense != after.Sense || before.Terminals != after.Terminals)
                throw new InvalidOperationException("Source state changed across independent voltage acquisition; resistance discarded.");
            if (step.RequiredRoute is { } expectedRoute)
                await ((MatrixSwitchSession)Get(expectedRoute.Device)).ConfirmHeldRouteAsync(Owner, token, expectedRoute.Closed);
            var currentAt = sourceFrame?.Timestamp ?? beforeAt;
            var evidence = new IndependentResistanceEvidence(step, before.Source, current, voltage.Value, currentAt, voltage.CapturedAt, beforeAt, afterAt);
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

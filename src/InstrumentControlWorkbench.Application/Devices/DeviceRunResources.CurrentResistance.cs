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
    public Task<(IndependentResistanceEvidence? Evidence, IReadOnlyList<AcquisitionQuality> Quality, IndependentResistanceFailure? Failure)> ReadCurrentResistanceAsync(
        ReadCurrentResistanceStep step, Quantity? expectedProgrammedCurrent, CancellationToken token, int? requiredMeterInput = null)
        => ReadCurrentResistanceAsync(step, expectedProgrammedCurrent, token, requiredMeterInput, null);

    internal async Task<(IndependentResistanceEvidence? Evidence, IReadOnlyList<AcquisitionQuality> Quality, IndependentResistanceFailure? Failure)> ReadCurrentResistanceAsync(
        ReadCurrentResistanceStep step, Quantity? expectedProgrammedCurrent, CancellationToken token, int? requiredMeterInput, Func<TimeSpan, Task>? retryDwell)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (expectedProgrammedCurrent is { } expected && (expected.Unit != PhysicalUnit.Ampere || Math.Abs(expected.Value) <= step.MinimumCurrent.Value))
            throw new ArgumentException("Expected programmed current must be defined Ampere excitation.");
        await gate.WaitAsync(token);
        var gateHeld = true;
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
            var configuration = before is not null ? new CurrentExcitationConfiguration(before.Source) : new CurrentExcitationConfiguration(CurrentSourceModel.Keithley6221, pureBefore!.Settings!.Current, pureBefore.Settings.Compliance, pureBefore.Settings.ManualRange, pureBefore.Settings.SourceDelay);
            if (expectedProgrammedCurrent is { } requested && configuration.Level != requested.Value) throw new InvalidOperationException("Current excitation differs from the expected programmed current.");
            SourceSenseReading? sourceFrame = null;
            if (step.CurrentReference == CurrentReference.MeasuredSourceReadback)
                sourceFrame = await manager!.ReadSourceSenseAsync(SourceFunction.Current, token, Owner, before!.Sense.Mode);
            var current = sourceFrame?.Source ?? new(configuration.Level, PhysicalUnit.Ampere);
            if (Math.Abs(current.Value) <= step.MinimumCurrent.Value) throw new InvalidOperationException("Current is zero or below the resistance threshold.");
            ParameterObservation voltage;
            CurrentResistanceRetryEvidence? retryEvidence = null;
            MeterAcquisitionException? priorFailure = null;
            try
            {
                try { voltage = await meter.ReadObservationAsync(Keithley2182Parameters.Voltage.Id, token, Owner, requiredMeterInput, classifyReadFailure: true); }
                catch (MeterAcquisitionException failure) when (step.Retry is { } retry && failure.Failure.AllowsSingleRetry && !token.IsCancellationRequested)
                {
                    priorFailure = failure;
                    // The read task is terminal. Cleanup has its own bounded lifetime,
                    // and each stop stays attached until terminal even on timeout.
                    var retryStops = retry.OwnsSourceOutput ? new[] { step.Source, step.Meter } : new[] { step.Meter };
                    foreach (var id in retryStops) pendingStops.Add(id, StopAndCaptureAsync(Get(id)));
                    var failures = await AwaitStopsAsync(retryStops, CancellationToken.None);
                    if (failures.Count != 0) throw new AggregateException("Acquisition retry cleanup failed; no second read or output restore is allowed.", new[] { failure }.Concat(failures.Select(f => f.Error)));
                    foreach (var id in retryStops) await PrepareMutationAsync(id);
                    token.ThrowIfCancellationRequested();
                    if (step.RequiredRoute is { } held) await ((MatrixSwitchSession)Get(held.Device)).ConfirmHeldRouteAsync(Owner, token, held.Closed);
                    var retrySource = manager is null ? null : await manager.CaptureRunSourceStateAsync(Owner, token);
                    var retryPure = pure is null ? null : await pure.CaptureSourceStateAsync(Owner, token);
                    var outputExpected = !retry.OwnsSourceOutput;
                    if (retrySource is not null && (!retrySource.Connected || retrySource.OutputEnabled != outputExpected || retrySource.Source != before!.Source || retrySource.Sense != before.Sense || retrySource.Terminals != before.Terminals) ||
                        retryPure is not null && (!retryPure.Connected || retryPure.OutputEnabled != outputExpected || retryPure.Settings != pureBefore!.Settings))
                        throw new InvalidOperationException("Excitation changed after failed acquisition; retry refused.", failure);
                    DateTimeOffset? offAt = retry.OwnsSourceOutput ? DateTimeOffset.UtcNow : null;
                    var retrySettings = await meter.CaptureResumeSettingsAsync(Owner, token);
                    if (retrySettings != failure.Settings) throw new InvalidOperationException("Meter settings changed after failed acquisition; retry refused.", failure);
                    var idleAt = DateTimeOffset.UtcNow;
                    if (retry.OwnsSourceOutput)
                    {
                        // Check every guard route, as normal enable does. Do not
                        // reconfigure or overwrite a physical operator change.
                        foreach (var matrix in sessions.Values.OfType<MatrixSwitchSession>().Where(m => m.Definition.GuardedSources.Contains(step.Source)))
                        {
                            await PrepareMutationAsync(matrix.Descriptor.Id); touched.Add(matrix.Descriptor.Id);
                            await matrix.ConfirmHeldRouteAsync(Owner, token);
                        }
                        await PrepareMutationAsync(step.Source);
                        if (manager is not null) await manager.EnterRunAsync(token, Owner, expectedResumeState: before);
                        else await pure!.EnableOutputAsync(Owner, token, pureBefore);
                    }
                    if (retryDwell is not null)
                    {
                        // Pause uses this same resource gate. The run retains its
                        // leases while its ordinary dwell parks/restores resources.
                        gate.Release(); gateHeld = false;
                        try { await retryDwell(retry.Settle); }
                        finally { await gate.WaitAsync(CancellationToken.None); gateHeld = true; }
                        RequireActive(); await PrepareMutationAsync(step.Source);
                    }
                    else if (retry.Settle > TimeSpan.Zero) await Task.Delay(retry.Settle, token);
                    var originalSource = before;
                    before = manager is null ? null : await manager.CaptureRunSourceStateAsync(Owner, token);
                    pureBefore = pure is null ? null : await pure.CaptureSourceStateAsync(Owner, token);
                    if (before is not null && (!before.Connected || !before.OutputEnabled || new CurrentExcitationConfiguration(before.Source) != configuration || before.Sense != originalSource!.Sense || before.Terminals != originalSource.Terminals) ||
                        pureBefore is not null && (!pureBefore.OutputEnabled || new CurrentExcitationConfiguration(CurrentSourceModel.Keithley6221, pureBefore.Settings!.Current, pureBefore.Settings.Compliance, pureBefore.Settings.ManualRange, pureBefore.Settings.SourceDelay) != configuration))
                        throw new InvalidOperationException("Excitation did not remain confirmed for the second acquisition.", failure);
                    if (step.RequiredRoute is { } secondRoute) await ((MatrixSwitchSession)Get(secondRoute.Device)).ConfirmHeldRouteAsync(Owner, token, secondRoute.Closed);
                    beforeAt = DateTimeOffset.UtcNow;
                    sourceFrame = step.CurrentReference == CurrentReference.MeasuredSourceReadback ? await manager!.ReadSourceSenseAsync(SourceFunction.Current, token, Owner, before!.Sense.Mode) : null;
                    current = sourceFrame?.Source ?? new(configuration.Level, PhysicalUnit.Ampere);
                    if (Math.Abs(current.Value) <= step.MinimumCurrent.Value) throw new InvalidOperationException("Retried source current is below the resistance threshold.");
                    await PrepareMutationAsync(step.Meter);
                    // No enclosing loop: a second failure is recovered only by explicit policy after fresh cleanup.
                    retryEvidence = new(failure.Failure.Kind, failure.Failure.CommandCode, failure.Message, failure.StartedAt, failure.FailedAt, idleAt, offAt, failure.Settings);
                    try { voltage = await meter.ReadObservationAsync(Keithley2182Parameters.Voltage.Id, token, Owner, requiredMeterInput, classifyReadFailure: true); }
                    catch (MeterAcquisitionException second) when (retry.ContinueOnAcquisitionFailure && second.Failure.AllowsInvalidPoint && !token.IsCancellationRequested) { throw; }
                    catch (Exception second) when (second is not OperationCanceledException && !token.IsCancellationRequested)
                    { throw new AggregateException("Both independent voltage acquisition attempts failed.", failure, second); }
                    if (voltage.MeterConfiguration != failure.Settings) throw new InvalidOperationException("Meter settings changed before the second acquisition; resistance discarded.", failure);
                }
            }
            catch (MeterAcquisitionException failure) when (step.Retry is { ContinueOnAcquisitionFailure: true } recovery && failure.Failure.AllowsInvalidPoint && !token.IsCancellationRequested)
            {
                var ids = recovery.OwnsSourceOutput ? new[] { step.Source, step.Meter } : new[] { step.Meter };
                foreach (var id in ids) pendingStops.Add(id, StopAndCaptureAsync(Get(id)));
                var cleanup = await AwaitStopsAsync(ids, CancellationToken.None);
                if (cleanup.Count != 0) throw new AggregateException("Invalid-point cleanup failed; scan continuation is forbidden.", (priorFailure is null ? new Exception[] { failure } : new Exception[] { priorFailure, failure }).Concat(cleanup.Select(f => f.Error)));
                // Completed proof is consumed by these fresh observations. A
                // newly observed unknown state cannot reuse the earlier stop.
                foreach (var id in ids) await PrepareMutationAsync(id);
                token.ThrowIfCancellationRequested();
                var confirmedSource = manager is null ? null : await manager.CaptureRunSourceStateAsync(Owner, token);
                var confirmedPure = pure is null ? null : await pure.CaptureSourceStateAsync(Owner, token);
                var outputExpected = !recovery.OwnsSourceOutput;
                if (confirmedSource is not null && (!confirmedSource.Connected || confirmedSource.OutputEnabled != outputExpected || confirmedSource.Source != before!.Source || confirmedSource.Sense != before.Sense || confirmedSource.Terminals != before.Terminals) ||
                    confirmedPure is not null && (!confirmedPure.Connected || confirmedPure.OutputEnabled != outputExpected || confirmedPure.Settings != pureBefore!.Settings))
                    throw new InvalidOperationException("Excitation drift prevents recording an invalid point.", failure);
                var sourceAt = DateTimeOffset.UtcNow;
                var settings = await meter.CaptureResumeSettingsAsync(Owner, token);
                if (settings != failure.Settings || requiredMeterInput is { } input && settings.Channel != input)
                    throw new InvalidOperationException("Meter drift prevents recording an invalid point.", failure);
                var idleAt = DateTimeOffset.UtcNow;
                if (step.RequiredRoute is { } invalidRoute) await ((MatrixSwitchSession)Get(invalidRoute.Device)).ConfirmHeldRouteAsync(Owner, token, invalidRoute.Closed);
                var invalid = new IndependentResistanceFailure(step, configuration, failure.Failure.Kind, failure.Failure.CommandCode, failure.Message, settings,
                    beforeAt, failure.StartedAt, failure.FailedAt, sourceAt, idleAt, recovery.OwnsSourceOutput ? sourceAt : null, retryEvidence);
                return (null, Array.Empty<AcquisitionQuality>(), invalid);
            }
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
            var evidence = new IndependentResistanceEvidence(step, configuration, current, voltage.Value, currentAt, voltage.CapturedAt, beforeAt, afterAt, retryEvidence);
            IReadOnlyList<AcquisitionQuality> quality = Array.AsReadOnly(new[]
            {
                new AcquisitionQuality(step.Id, step.Source, step.CurrentColumn, currentAt, sourceFrame?.Status, sourceFrame?.AcquisitionConfiguration),
                new AcquisitionQuality(step.Id, step.Meter, step.VoltageColumn, voltage.CapturedAt, voltage.Status, voltage.AcquisitionConfiguration, voltage.MeterConfiguration),
            });
            return (evidence, quality, null);
        }
        finally { if (gateHeld) gate.Release(); }
    }
}

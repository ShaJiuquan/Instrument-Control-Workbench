using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task<StepOutcome> ExecuteSoftwareTemperatureScanAsync(SoftwareTemperatureScanStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        var first = step.Points.Values[0]; var last = step.Points.Values[^1]; var increasing = last.Value > first.Value;
        var trajectory = new SoftwareTemperatureTrajectory(first, last, step.Rate);
        var previousAxes = new Dictionary<ParameterId, Quantity>(run.Axes);
        var actualRate = await run.Resources!.PrepareSoftwareScanAsync(step.Device, first, run.Token);
        if (step.WaitStableFirst) await WaitScanFirstTemperatureAsync(step, run, actualRate);
        run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
        await run.Resources.ActivateSoftwareScanAsync(step.Device, first, last, step.Rate, actualRate, run.Token);
        var rampStarted = run.ActiveElapsed; var programmed = first; TimeSpan? reachedEnd = null; TimeSpan? lastSample = null; var index = 0; var endpointObserved = false;
        bool pointMode = step.Sampling is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint;
        void CheckTail()
        {
            if (!pointMode && !endpointObserved && reachedEnd is { } end && run.ActiveElapsed - end > step.StableTimeout)
                throw new TimeoutException($"{step.Id}: software ramp endpoint not observed before guard timeout.");
        }
        async Task<AxisControllerSnapshot> AdvanceAsync()
        {
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run); CheckTail();
            var next = trajectory.At(run.ActiveElapsed - rampStarted);
            var observation = await run.Resources.AdvanceSoftwareScanAsync(step.Device, programmed, next, actualRate, last, step.Rate, run.Token);
            CheckTail(); // Discard a first arrival returned after the deadline.
            programmed = next;
            if (next == trajectory.RoundedLast) reachedEnd ??= run.ActiveElapsed;
            endpointObserved = reachedEnd is not null && (increasing ? observation.Readback.Value >= last.Value - step.Tolerance.Value : observation.Readback.Value <= last.Value + step.Tolerance.Value);
            CheckTail(); return observation;
        }
        try
        {
            while (true)
            {
                run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run); AdvancePlanExecution(run, step.Id);
                if (step.Sampling == TemperatureRampSampling.Timed && lastSample is { } previous)
                {
                    while (true)
                    {
                        _ = await AdvanceAsync();
                        var remaining = step.Interval - (run.Elapsed.Elapsed - previous); if (remaining <= TimeSpan.Zero) break;
                        if (!endpointObserved && reachedEnd is { } end && step.StableTimeout - (run.ActiveElapsed - end) < remaining) remaining = step.StableTimeout - (run.ActiveElapsed - end);
                        await DwellAsync(run, remaining < step.PollInterval ? remaining : step.PollInterval);
                    }
                }
                var waitingSince = run.ActiveElapsed; AxisControllerSnapshot snapshot;
                while (true)
                {
                    snapshot = await AdvanceAsync();
                    if (!pointMode) break;
                    if (run.ActiveElapsed - waitingSince > step.StepTimeout) throw new TimeoutException($"{step.Id}: software ramp threshold timeout.");
                    var compared = step.Sampling == TemperatureRampSampling.EstimatedSetpoint ? snapshot.Target.Value : snapshot.Readback.Value;
                    var threshold = step.Points.Values[index].Value;
                    if (increasing ? compared >= threshold : compared <= threshold) break;
                    await DwellAsync(run, step.PollInterval);
                }
                lastSample = run.Elapsed.Elapsed;
                run.Axes[step.Column] = snapshot.Readback; run.Axes[step.SetpointColumn] = snapshot.Target;
                run.Axes[step.IndexColumn] = new(index, PhysicalUnit.Dimensionless); run.Axes[step.TotalColumn] = new(step.ProgressTotal, PhysicalUnit.Dimensionless);
                foreach (var column in new[] { step.Column, step.SetpointColumn, step.IndexColumn, step.TotalColumn }) run.SetVariable(column, run.Axes[column], step.Id);
                var outcome = await ExecuteStepAsync(step.Body, run, listeners, ivPlot); if (outcome == StepOutcome.Failure) return outcome;
                var pathDone = programmed == trajectory.RoundedLast;
                var actualDone = increasing ? snapshot.Readback.Value >= last.Value - step.Tolerance.Value : snapshot.Readback.Value <= last.Value + step.Tolerance.Value;
                if (pointMode ? index == step.Points.Values.Count - 1 && pathDone : pathDone && actualDone)
                {
                    run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                    await run.Resources.CompleteSoftwareScanAsync(step.Device, programmed, actualRate, run.Token); return StepOutcome.Success;
                }
                // PPMS can sample beyond the final threshold until the programmed trajectory finishes.
                // Keep that final point's index; the run-wide execution budget bounds extra samples.
                if (!pointMode || index < step.Points.Values.Count - 1) index++;
            }
        }
        finally { RestoreAxes(run, previousAxes); }
    }
}

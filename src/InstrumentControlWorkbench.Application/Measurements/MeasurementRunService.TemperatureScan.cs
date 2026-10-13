using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task<StepOutcome> ExecuteHardwareTemperatureScanAsync(HardwareTemperatureScanStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        var first = step.Points.Values[0]; var last = step.Points.Values[^1]; var increasing = last.Value > first.Value;
        var previousAxes = new Dictionary<ParameterId, Quantity>(run.Axes);
        await run.Resources!.PrepareTemperatureScanAsync(step.Device, first, step.Rate, run.Token);
        if (step.WaitStableFirst) await WaitScanFirstTemperatureAsync(step, run);
        run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
        await run.Resources.StartTemperatureScanAsync(step.Device, first, last, step.Rate, run.Token);
        var rampStarted = run.ActiveElapsed; TimeSpan? lastSample = null; var index = 0;
        try
        {
            while (true)
            {
                run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run); AdvancePlanExecution(run, step.Id);
                if (step.Sampling == TemperatureRampSampling.Timed && lastSample is { } previous) await WaitTemperatureScanIntervalAsync(step, run, previous, rampStarted);
                var waitingSince = run.ActiveElapsed; Core.Devices.AxisControllerSnapshot snapshot;
                while (true)
                {
                    run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                    var active = run.ActiveElapsed;
                    if (step.Sampling is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint && active - waitingSince > step.StepTimeout) throw new TimeoutException($"{step.Id}: ramp threshold timeout.");
                    if (step.Sampling is TemperatureRampSampling.Timed or TemperatureRampSampling.Immediate && active - rampStarted > step.NominalDuration + step.StableTimeout) throw new TimeoutException($"{step.Id}: ramp endpoint not observed before guard timeout.");
                    snapshot = await run.Resources.ObserveTemperatureScanAsync(step.Device, last, step.Rate, true, run.Token);
                    if (step.Sampling is TemperatureRampSampling.Timed or TemperatureRampSampling.Immediate) break;
                    var compared = step.Sampling == TemperatureRampSampling.EstimatedSetpoint ? first.Value + (increasing ? 1 : -1) * step.Rate.Value / 60 * (run.ActiveElapsed - rampStarted).TotalSeconds : snapshot.Readback.Value;
                    if (increasing ? compared >= step.Points.Values[index].Value : compared <= step.Points.Values[index].Value) break;
                    await DwellAsync(run, step.PollInterval);
                }
                if (step.Sampling is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint && run.ActiveElapsed - waitingSince > step.StepTimeout || step.Sampling is TemperatureRampSampling.Timed or TemperatureRampSampling.Immediate && run.ActiveElapsed - rampStarted > step.NominalDuration + step.StableTimeout) throw new TimeoutException($"{step.Id}: late ramp sample discarded.");
                lastSample = run.Elapsed.Elapsed;
                run.Axes[step.Column] = snapshot.Readback; run.Axes[step.IndexColumn] = new(index, PhysicalUnit.Dimensionless); run.Axes[step.TotalColumn] = new(step.ProgressTotal, PhysicalUnit.Dimensionless);
                foreach (var column in new[] { step.Column, step.IndexColumn, step.TotalColumn }) run.SetVariable(column, run.Axes[column], step.Id);
                var outcome = await ExecuteStepAsync(step.Body, run, listeners, ivPlot); if (outcome == StepOutcome.Failure) return outcome;
                index++;
                var done = step.Sampling is TemperatureRampSampling.Threshold or TemperatureRampSampling.EstimatedSetpoint ? index >= step.Points.Values.Count : increasing ? snapshot.Readback.Value >= last.Value - step.Tolerance.Value : snapshot.Readback.Value <= last.Value + step.Tolerance.Value;
                if (done)
                {
                    run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run); await run.Resources.CompleteTemperatureScanAsync(step.Device, last, step.Rate, run.Token); return StepOutcome.Success;
                }
            }
        }
        finally { RestoreAxes(run, previousAxes); }
    }
    private async Task WaitScanFirstTemperatureAsync(HardwareTemperatureScanStep step, RunExecutionContext run)
    {
        var since = run.ActiveElapsed; var consecutive = 0; var pauseVersion = run.PauseVersion;
        while (true)
        {
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
            if (pauseVersion != run.PauseVersion) { consecutive = 0; pauseVersion = run.PauseVersion; }
            if (run.ActiveElapsed - since > step.StableTimeout) throw new TimeoutException($"{step.Id}: first temperature did not stabilize.");
            var snapshot = await run.Resources!.ObserveTemperatureScanAsync(step.Device, step.Points.Values[0], step.Rate, false, run.Token);
            consecutive = Math.Abs(snapshot.Readback.Value - step.Points.Values[0].Value) < step.Tolerance.Value ? consecutive + 1 : 0;
            if (run.ActiveElapsed - since > step.StableTimeout) throw new TimeoutException($"{step.Id}: late first-temperature observation discarded.");
            if (consecutive >= step.StableCount) return; await DwellAsync(run, step.PollInterval);
        }
    }
    private async Task WaitTemperatureScanIntervalAsync(HardwareTemperatureScanStep step, RunExecutionContext run, TimeSpan previous, TimeSpan rampStarted)
    {
        while (true)
        {
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
            var guardRemaining = step.NominalDuration + step.StableTimeout - (run.ActiveElapsed - rampStarted);
            if (guardRemaining <= TimeSpan.Zero) throw new TimeoutException($"{step.Id}: ramp endpoint guard expired while waiting for next sample.");
            var remaining = step.Interval - (run.Elapsed.Elapsed - previous); if (remaining <= TimeSpan.Zero) return;
            if (guardRemaining < remaining) remaining = guardRemaining;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            var timer = Task.Delay(remaining < step.PollInterval ? remaining : step.PollInterval, cancellation.Token); var winner = await Task.WhenAny(timer, run.PauseRequested);
            if (winner == timer && run.PauseGate is null) { await timer; continue; }
            cancellation.Cancel(); try { await timer; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
        }
    }
}

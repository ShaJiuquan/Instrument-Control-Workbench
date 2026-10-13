using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task<StepOutcome> ExecuteCoolingAsync(TemperatureCoolingStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started, bool ivPlot)
    {
        var previousAxes = new Dictionary<ParameterId, Quantity>(run.Axes);
        var rate = await run.Resources!.BeginCoolingAsync(step.Device, step.CoolingTarget, run.Token);
        TimeSpan? lastSample = null; var index = 0;
        try
        {
            while (true)
            {
                run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                AdvancePlanExecution(run, step.Id);
                if (step.Sampling == CoolingSamplingMode.Timed && lastSample is { } previous)
                {
                    // Minimum interval between sample starts, including child acquisition time.
                    await WaitCoolingIntervalAsync(step, run, previous);
                }
                var waitingSince = run.Elapsed.Elapsed;
                Core.Devices.AxisControllerSnapshot snapshot;
                while (true)
                {
                    run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                    if (step.Sampling == CoolingSamplingMode.Threshold && run.Elapsed.Elapsed - waitingSince > step.Timeout) throw new TimeoutException($"{step.Id}: cooling threshold timeout.");
                    snapshot = await run.Resources.ObserveCoolingAsync(step.Device, step.CoolingTarget, rate, run.Token);
                    if (step.Sampling != CoolingSamplingMode.Threshold || snapshot.Readback.Value <= step.Thresholds.Values[index].Value + step.Tolerance.Value || snapshot.Readback.Value <= step.EndTemperature.Value + step.Tolerance.Value) break;
                    var remaining = step.Timeout - (run.Elapsed.Elapsed - waitingSince);
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException($"{step.Id}: cooling threshold timeout.");
                    await DwellAsync(run, remaining < step.PollInterval ? remaining : step.PollInterval);
                }
                if (step.Sampling == CoolingSamplingMode.Threshold && run.Elapsed.Elapsed - waitingSince > step.Timeout) throw new TimeoutException($"{step.Id}: late cooling observation discarded.");
                lastSample = run.Elapsed.Elapsed;
                var total = step.Sampling == CoolingSamplingMode.Threshold ? step.Thresholds.Values.Count : 0;
                run.Axes[step.Column] = snapshot.Readback; run.Axes[step.IndexColumn] = new(index, PhysicalUnit.Dimensionless); run.Axes[step.TotalColumn] = new(total, PhysicalUnit.Dimensionless);
                foreach (var column in new[] { step.Column, step.IndexColumn, step.TotalColumn }) run.SetVariable(column, run.Axes[column], step.Id);
                var outcome = await ExecuteStepAsync(step.Body, run, started, ivPlot);
                if (outcome == StepOutcome.Failure) return outcome; // OFF obligation remains attached through root cleanup/recovery.
                index++;
                if (snapshot.Readback.Value <= step.EndTemperature.Value + step.Tolerance.Value || step.Sampling == CoolingSamplingMode.Threshold && index >= step.Thresholds.Values.Count)
                {
                    run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                    await run.Resources.CompleteCoolingAsync(step.Device, step.CoolingTarget, rate, run.Token); return StepOutcome.Success;
                }
            }
        }
        finally { RestoreAxes(run, previousAxes); }
    }
    private async Task WaitCoolingIntervalAsync(TemperatureCoolingStep step, RunExecutionContext run, TimeSpan previous)
    {
        while (true)
        {
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
            var remaining = step.Interval - (run.Elapsed.Elapsed - previous);
            if (remaining <= TimeSpan.Zero) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            var timer = Task.Delay(remaining < step.PollInterval ? remaining : step.PollInterval, cancellation.Token);
            var winner = await Task.WhenAny(timer, run.PauseRequested);
            if (winner == timer && run.PauseGate is null) { await timer; continue; }
            cancellation.Cancel(); try { await timer; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
            // Natural cooling and the interval keep advancing in wall time during pause.
        }
    }

}

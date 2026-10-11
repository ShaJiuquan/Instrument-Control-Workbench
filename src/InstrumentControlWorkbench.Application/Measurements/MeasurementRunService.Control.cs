using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task<StepOutcome> ExecuteTimeoutAsync(TimeoutStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        var parentToken = run.Token;
        var previousScope = run.ScopedToken;
        using var deadline = new CancellationTokenSource();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(parentToken, deadline.Token);
        if (step.Timeout == TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(step.Timeout);
        run.ScopedToken = scope.Token;
        try
        {
            // Await the real child to completion. Dropping a timed-out task
            // would let cleanup/release overlap live instrument IO.
            var outcome = await ExecuteStepAsync(step.Body, run, listeners, ivPlot);
            parentToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new PlanStepTimeoutException(step.Id, step.Timeout);
            return outcome;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !parentToken.IsCancellationRequested)
        {
            parentToken.ThrowIfCancellationRequested();
            throw new PlanStepTimeoutException(step.Id, step.Timeout, error);
        }
        finally { run.ScopedToken = previousScope; }
    }

    private async Task<StepOutcome> ExecuteFallbackAsync(FallbackStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        var axes = new Dictionary<ParameterId, Quantity>(run.Axes);
        var variables = new Dictionary<ParameterId, Quantity>(run.Variables);
        try
        {
            for (var i = 0; i < step.Children.Count; i++)
            {
                run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                run.Variables.Clear(); foreach (var item in variables) run.Variables.Add(item.Key, item.Value);
                run.Variables[step.BranchColumn] = run.Axes[step.BranchColumn] = new(i + 1, PhysicalUnit.Dimensionless);
                run.Variables[step.TotalColumn] = run.Axes[step.TotalColumn] = new(step.Children.Count, PhysicalUnit.Dimensionless);
                if (await ExecuteStepAsync(step.Children[i], run, listeners, ivPlot) == StepOutcome.Success) return StepOutcome.Success;
            }
            return StepOutcome.Failure;
        }
        finally { RestoreAxes(run, axes); }
    }

    private async Task<StepOutcome> ExecuteRetryAsync(RetryStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        var axes = new Dictionary<ParameterId, Quantity>(run.Axes);
        var variables = new Dictionary<ParameterId, Quantity>(run.Variables);
        try
        {
            for (var i = 0; i < step.MaxAttempts; i++)
            {
                run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
                run.Variables.Clear(); foreach (var item in variables) run.Variables.Add(item.Key, item.Value);
                run.Variables[step.AttemptColumn] = run.Axes[step.AttemptColumn] = new(i + 1, PhysicalUnit.Dimensionless);
                run.Variables[step.TotalColumn] = run.Axes[step.TotalColumn] = new(step.MaxAttempts, PhysicalUnit.Dimensionless);
                if (await ExecuteStepAsync(step.Body, run, listeners, ivPlot) == StepOutcome.Success) return StepOutcome.Success;
                if (i + 1 < step.MaxAttempts) await DwellAsync(run, step.Delay);
            }
            return StepOutcome.Failure;
        }
        finally { RestoreAxes(run, axes); }
    }
}

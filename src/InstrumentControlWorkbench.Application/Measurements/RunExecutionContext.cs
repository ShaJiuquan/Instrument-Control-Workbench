using System.Diagnostics;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Application.Measurements;

public enum RunExecutionPhase
{
    Idle, Starting, Running, Paused, Finalizing, SafetyRecoveryRequired,
}

public sealed record RunOutcome(RunId Id, MeasurementRunState State, IReadOnlyList<Exception> Errors);

// Each admitted run owns its cancellation, timing, pause gate and pending cleanup.
// Pending cleanup remains attached after a timeout so recovery cannot overlap it.
internal sealed class RunExecutionContext : IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource? resume;
    private TaskCompletionSource pauseRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancellation;

    public RunExecutionContext(CancellationToken token)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public RunId Id { get; } = RunId.New();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    public CancellationToken Token => cancellation.Token;
    public volatile RunExecutionPhase Phase = RunExecutionPhase.Starting;
    public bool InstrumentClaimed { get; set; }
    public Task<Exception?>? SafetyTask { get; set; }
    public RunDataSet? DataSet { get; set; }

    public void Pause()
    {
        lock (gate)
        {
            if (Phase is not (RunExecutionPhase.Starting or RunExecutionPhase.Running or RunExecutionPhase.Paused))
                throw new InvalidOperationException("Only an acquiring run can be paused.");
            resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            pauseRequested.TrySetResult();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            resume?.TrySetResult();
            resume = null;
            pauseRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task? PauseGate { get { lock (gate) return resume?.Task; } }
    public Task PauseRequested { get { lock (gate) return pauseRequested.Task; } }
    public void Abort() => cancellation.Cancel();
    public void Dispose() => cancellation.Dispose();
}

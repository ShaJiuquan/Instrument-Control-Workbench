using System.Diagnostics;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

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
    public DeviceRunResources? Resources { get; set; }
    public Dictionary<ParameterId, Quantity> Variables { get; } = [];
    public Dictionary<ParameterId, Quantity> Axes { get; } = [];
    public TimeSpan PausedDuration { get; set; }
    public TimeSpan ActiveElapsed => Elapsed.Elapsed - PausedDuration;
    public int PauseVersion { get; set; }
    public int RowsAcquired { get; set; }
    public List<MeasurementDataRow>? MeasurementInputs { get; set; }
    public MeasurementDataRow? LastRow { get; set; }
    public long ExecutedSteps { get; set; }
    public RunDataSet? DataSet { get; set; }
    public IReadOnlyDictionary<StepId, ResolvedSampleRoute> SampleRoutes { get; set; } = new Dictionary<StepId, ResolvedSampleRoute>();
    public TaskCompletionSource Finalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

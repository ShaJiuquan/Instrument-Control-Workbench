using System.Runtime.CompilerServices;
using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private RunBlackboardState? lastBlackboardState;
    public RunId? ActiveRunId { get { lock (admissionGate) return active?.Id; } }

    // Null revision reads now; an observed revision waits for newer state.
    // Intermediate writes may coalesce. No instrument IO is performed here.
    public Task<RunBlackboardObservation> ObserveBlackboardAsync(RunId id, long? afterRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        return ResolveBlackboardState(id).ReadAsync(afterRevision, cancellationToken);
    }

    // The enumerator pins one generation even if a subsequent run completes.
    // Consumers own iteration/cancellation; no background task is created.
    public async IAsyncEnumerable<RunBlackboardObservation> WatchBlackboardAsync(RunId id,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id); cancellationToken.ThrowIfCancellationRequested();
        var state = ResolveBlackboardState(id);
        long? revision = null;
        while (true)
        {
            var observation = await state.ReadAsync(revision, cancellationToken);
            yield return observation;
            if (observation.IsTerminal) yield break;
            revision = observation.Revision;
        }
    }

    // Ownership boundary shared by immediate reads and pinned streams.
    private RunBlackboardState ResolveBlackboardState(RunId id)
    {
        RunBlackboardState state;
        lock (admissionGate)
            state = active?.Id == id ? active.Blackboard : lastBlackboardState?.Id == id ? lastBlackboardState :
                throw new InvalidOperationException("Blackboard observation requires the active run or the most recently finalized run ID.");
        return state;
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

// One sequential producer; concurrent diagnostic consumers get frozen copies.
// A shared change signal coalesces writes without retaining a consumer queue.
internal sealed class RunBlackboardState
{
    private readonly object gate = new();
    private readonly Stopwatch clock;
    private readonly Dictionary<ParameterId, BlackboardEntry> entries = [];
    private readonly List<StepId> scopes = [];
    private TaskCompletionSource? changed;
    private long revision;
    private RunExecutionPhase phase = RunExecutionPhase.Starting;
    private MeasurementRunState? outcome;
    private RunBlackboardSnapshot? terminalSnapshot;

    public RunBlackboardState(RunId id, Stopwatch clock)
    { Id = id; this.clock = clock; Variables = new ReadOnlyDictionary<ParameterId, BlackboardEntry>(entries); }
    public RunId Id { get; }
    // Only the sequential executor reads this view. Consumers must use ReadAsync.
    public IReadOnlyDictionary<ParameterId, BlackboardEntry> Variables { get; }
    public RunExecutionPhase Phase
    {
        get { lock (gate) return phase; }
        set { lock (gate) { if (phase == value) return; phase = value; Advance(); } }
    }

    public void Set(ParameterId id, BlackboardValue value, StepId source, BlackboardWriteKind kind)
    {
        lock (gate)
        {
            entries[id] = new(value, source, DateTimeOffset.UtcNow, clock.Elapsed, kind);
            Advance();
        }
    }
    public void Remove(ParameterId id)
    { lock (gate) { if (entries.Remove(id)) Advance(); } }
    public Dictionary<ParameterId, BlackboardEntry> CopyEntries()
    { lock (gate) return new(entries); }
    public RunBlackboardSnapshot Capture()
    { lock (gate) return new(Id, DateTimeOffset.UtcNow, clock.Elapsed, entries); }

    public void EnterScope(StepId scope)
    { lock (gate) { scopes.Add(scope); Advance(); } }
    public void RestoreEntries(IReadOnlyDictionary<ParameterId, BlackboardEntry> parent, StepId? leavingScope = null)
    {
        lock (gate)
        {
            if (leavingScope is not null)
            {
                if (scopes.Count == 0 || scopes[^1] != leavingScope) throw new InvalidOperationException("Blackboard scope ownership changed unexpectedly.");
                scopes.RemoveAt(scopes.Count - 1);
            }
            entries.Clear(); foreach (var item in parent) entries.Add(item.Key, item.Value);
            Advance();
        }
    }
    public void Complete(MeasurementRunState result, RunBlackboardSnapshot? snapshot)
    {
        lock (gate)
        {
            terminalSnapshot = snapshot; outcome = result;
            Advance();
        }
    }
    public async Task<RunBlackboardObservation> ReadAsync(long? afterRevision, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task signal;
            lock (gate)
            {
                if (afterRevision < 0 || afterRevision > revision) throw new ArgumentOutOfRangeException(nameof(afterRevision), "Revision must belong to an already observed state of this run.");
                if (afterRevision is null || revision > afterRevision || outcome is not null)
                    return new(Id, revision, phase, outcome is null ? new(Id, DateTimeOffset.UtcNow, clock.Elapsed, entries) : terminalSnapshot,
                        Array.AsReadOnly(scopes.ToArray()), outcome);
                changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = changed.Task;
            }
            // Each wait has its own cancellation; the shared producer survives.
            await signal.WaitAsync(token);
        }
    }
    private void Advance()
    {
        revision++;
        changed?.TrySetResult(); changed = null;
    }
}

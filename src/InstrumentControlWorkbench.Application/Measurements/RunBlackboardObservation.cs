using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Application.Measurements;

// A coherent observation, not a hardware status or a journal of every write.
public sealed record RunBlackboardObservation
{
    internal RunBlackboardObservation(RunId id, long revision, RunExecutionPhase phase, RunBlackboardSnapshot? snapshot,
        IReadOnlyList<StepId> scopes, MeasurementRunState? outcome)
    { Id = id; Revision = revision; Phase = phase; Snapshot = snapshot; Scopes = scopes; Outcome = outcome; }
    public RunId Id { get; }
    public long Revision { get; }
    public RunExecutionPhase Phase { get; }
    public RunBlackboardSnapshot? Snapshot { get; }
    public IReadOnlyList<StepId> Scopes { get; }
    public MeasurementRunState? Outcome { get; }
    // Producer completion does not prove resource release or safe hardware.
    public bool IsTerminal => Outcome is not null;
}

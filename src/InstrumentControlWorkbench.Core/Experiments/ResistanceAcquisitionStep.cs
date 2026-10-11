using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Sample topology and analysis consume this acquisition contract. Concrete
// steps retain their own instrument roles and observation evidence.
public abstract record ResistanceAcquisitionStep(StepId Id) : ExperimentStep(Id)
{
    public abstract InstrumentId Device { get; }
    public abstract SourceFunction Function { get; }
    public abstract ParameterId VoltageColumn { get; }
    public abstract ParameterId CurrentColumn { get; }
    public abstract ParameterId ResistanceColumn { get; }
    public abstract Quantity MinimumCurrent { get; }
    public abstract IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
}

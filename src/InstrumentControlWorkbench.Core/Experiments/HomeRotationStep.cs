using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Dedicated mechanical home, never a target-zero substitute. TimeoutStep owns
// the deadline so native callers can choose it explicitly.
public sealed record HomeRotationStep : ExperimentStep
{
    public HomeRotationStep(StepId id, InstrumentId device, Quantity tolerance, TimeSpan pollInterval) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (tolerance.Unit != PhysicalUnit.Degree || tolerance.Value <= 0) throw new ArgumentException("Homing needs a positive angle tolerance in degrees.");
        ExperimentDurations.Validate(pollInterval, positive: true);
        Device = device; Tolerance = tolerance; PollInterval = pollInterval;
    }
    public InstrumentId Device { get; }
    public Quantity Tolerance { get; }
    public TimeSpan PollInterval { get; }
}

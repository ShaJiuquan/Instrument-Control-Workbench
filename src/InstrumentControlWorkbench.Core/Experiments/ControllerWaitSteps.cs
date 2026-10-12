using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Wait for the live controller state, without programming or pinning a target.
// A native TimeoutStep owns any deadline; absence of that wrapper is explicit.
public sealed record WaitTemperatureStateStep : ExperimentStep
{
    public WaitTemperatureStateStep(StepId id, InstrumentId device, TemperatureWaitMode mode, TimeSpan pollInterval) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ExperimentDurations.Validate(pollInterval, positive: true);
        Device = device; Mode = mode; PollInterval = pollInterval;
    }
    public InstrumentId Device { get; }
    public TemperatureWaitMode Mode { get; }
    public TimeSpan PollInterval { get; }
}

// Null Target follows the freshly observed controller target on every poll.
// Explicit Target is a comparison threshold; this step never writes it.
public sealed record WaitAngleStep : ExperimentStep
{
    public WaitAngleStep(StepId id, InstrumentId device, Quantity tolerance, TimeSpan pollInterval, PlanValue? target = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (tolerance.Unit != PhysicalUnit.Degree || tolerance.Value < 0) throw new ArgumentException("Angle tolerance must be nonnegative Degree.", nameof(tolerance));
        if (target is ConstantValue literal && literal.Value.Unit != PhysicalUnit.Degree) throw new ArgumentException("Angle target must use Degree.", nameof(target));
        ExperimentDurations.Validate(pollInterval, positive: true);
        Device = device; Tolerance = tolerance; PollInterval = pollInterval; Target = target;
    }
    public InstrumentId Device { get; }
    public Quantity Tolerance { get; }
    public TimeSpan PollInterval { get; }
    public PlanValue? Target { get; }
}

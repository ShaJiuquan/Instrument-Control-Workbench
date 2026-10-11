using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record TemperatureStabilityStep : ExperimentStep
{
    public TemperatureStabilityStep(StepId id, InstrumentId device, TemperatureStabilityChange change) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(change);
        Device = device; Change = change;
    }
    public InstrumentId Device { get; }
    public TemperatureStabilityChange Change { get; }
}

public enum TemperatureWaitMode { Reached, Stable }

public sealed record WaitTemperatureStep : ExperimentStep
{
    public WaitTemperatureStep(StepId id, InstrumentId device, PlanValue target, TemperatureWaitMode mode, TimeSpan pollInterval, TimeSpan timeout) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ExperimentDurations.Validate(pollInterval, positive: true); ExperimentDurations.Validate(timeout, positive: true);
        Device = device; Target = target; Mode = mode; PollInterval = pollInterval; Timeout = timeout;
    }
    public InstrumentId Device { get; }
    public PlanValue Target { get; }
    public TemperatureWaitMode Mode { get; }
    public TimeSpan PollInterval { get; }
    public TimeSpan Timeout { get; }
}

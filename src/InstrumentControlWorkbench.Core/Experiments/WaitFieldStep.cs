using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Compare the live Tesla target/readback while the adapter reports no ramp.
// Vendor magnet-switch/persistent-state certification is a separate contract.
public sealed record WaitFieldStep : ExperimentStep
{
    public WaitFieldStep(StepId id, InstrumentId device, Quantity tolerance, TimeSpan stableFor, TimeSpan pollInterval) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (tolerance.Unit != PhysicalUnit.Tesla || tolerance.Value < 0) throw new ArgumentException("Field tolerance requires nonnegative Tesla.", nameof(tolerance));
        ExperimentDurations.Validate(stableFor); ExperimentDurations.Validate(pollInterval, positive: true);
        Device = device; Tolerance = tolerance; StableFor = stableFor; PollInterval = pollInterval;
    }
    public InstrumentId Device { get; }
    public Quantity Tolerance { get; }
    public TimeSpan StableFor { get; }
    public TimeSpan PollInterval { get; }
}

using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record HardwareTemperatureSetpointStep : ExperimentStep
{
    public HardwareTemperatureSetpointStep(StepId id, InstrumentId device, PlanValue target, Quantity rate, bool enableControl = true) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(target);
        TemperatureRampPath.ValidateRate(rate);
        if (target is ConstantValue literal) TemperatureRampPath.ValidateTemperature(literal.Value);
        Device = device; Target = target; Rate = rate; EnableControl = enableControl;
    }
    public InstrumentId Device { get; }
    public PlanValue Target { get; }
    public Quantity Rate { get; }
    // False preserves the observed control flag; it does not turn control off.
    public bool EnableControl { get; }
}

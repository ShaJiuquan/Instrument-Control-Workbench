using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record TemperatureControlStep : ExperimentStep
{
    public TemperatureControlStep(StepId id, InstrumentId device, TemperatureControlAction action, Quantity? rate = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if ((action == TemperatureControlAction.RampOn) != (rate is not null))
            throw new ArgumentException("RampOn requires a rate; other temperature-control commands must omit it.", nameof(rate));
        if (rate is { } requested) TemperatureRampPath.ValidateRate(requested);
        Device = device; Action = action; Rate = rate;
    }
    public InstrumentId Device { get; }
    public TemperatureControlAction Action { get; }
    public Quantity? Rate { get; }
}

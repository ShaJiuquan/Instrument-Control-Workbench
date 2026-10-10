using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record ReadResistanceStep : ExperimentStep
{
    public ReadResistanceStep(StepId id, InstrumentId device, SourceFunction function,
        ParameterId voltageColumn, ParameterId currentColumn, ParameterId resistanceColumn, Quantity minimumCurrent) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (!Enum.IsDefined(function)) throw new ArgumentOutOfRangeException(nameof(function));
        ArgumentNullException.ThrowIfNull(voltageColumn); ArgumentNullException.ThrowIfNull(currentColumn); ArgumentNullException.ThrowIfNull(resistanceColumn);
        if (new[] { voltageColumn, currentColumn, resistanceColumn }.Distinct().Count() != 3)
            throw new ArgumentException("Voltage, current and resistance columns must differ.");
        if (minimumCurrent.Unit != PhysicalUnit.Ampere || minimumCurrent.Value < 0)
            throw new ArgumentException("Minimum absolute current must be a nonnegative Ampere quantity.", nameof(minimumCurrent));
        Device = device; Function = function; VoltageColumn = voltageColumn; CurrentColumn = currentColumn;
        ResistanceColumn = resistanceColumn; MinimumCurrent = minimumCurrent;
    }
    public InstrumentId Device { get; }
    public SourceFunction Function { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public ParameterId ResistanceColumn { get; }
    public Quantity MinimumCurrent { get; }
}

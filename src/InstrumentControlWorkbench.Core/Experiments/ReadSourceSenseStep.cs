using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record ReadSourceSenseStep : ExperimentStep
{
    public ReadSourceSenseStep(StepId id, InstrumentId device, SourceFunction function, ParameterId voltageColumn, ParameterId currentColumn, IEnumerable<EnvironmentRead>? environmentReads = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (!Enum.IsDefined(function)) throw new ArgumentOutOfRangeException(nameof(function));
        ArgumentNullException.ThrowIfNull(voltageColumn); ArgumentNullException.ThrowIfNull(currentColumn);
        if (voltageColumn == currentColumn) throw new ArgumentException("Paired voltage/current columns must differ.");
        Device = device; Function = function; VoltageColumn = voltageColumn; CurrentColumn = currentColumn;
        EnvironmentReads = EnvironmentRead.Freeze(environmentReads, [voltageColumn, currentColumn]);
    }
    public InstrumentId Device { get; }
    public SourceFunction Function { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
}

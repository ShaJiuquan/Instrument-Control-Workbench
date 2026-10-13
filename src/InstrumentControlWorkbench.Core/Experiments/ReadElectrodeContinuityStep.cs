using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum ElectrodePair { Source, Meter, Bias, Gate }
public enum ElectrodeContinuityStatus { Connected, Open, NotConfigured }
public enum ElectrodeContinuityReason { Resistance, ZeroCurrent, Overrange, NotConfigured }

// One channel/pair observation. Null route explicitly means no configured pair.
public sealed record ReadElectrodeContinuityStep : ExperimentStep
{
    public ReadElectrodeContinuityStep(StepId id, SourcePreparation source, ChannelProfileId channel, ElectrodePair pair,
        MatrixRouteExpectation? route, TimeSpan settle, Quantity maximumConnectedResistance,
        ParameterId statusColumn, ParameterId reasonColumn, ParameterId voltageColumn, ParameterId currentColumn, ParameterId resistanceColumn) : base(id)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(channel);
        var columns = new[] { statusColumn, reasonColumn, voltageColumn, currentColumn, resistanceColumn };
        if (columns.Any(c => c is null) || columns.Distinct().Count() != 5 || !Enum.IsDefined(pair)) throw new ArgumentException("Continuity needs a defined pair and five distinct columns.");
        if (source.Function != SourceFunction.Voltage || source.Sense.Function != SenseFunction.Current || source.Sense.Mode != SensingMode.TwoWire || source.ManualSourceRange is not null)
            throw new ArgumentException("Continuity requires an autoranged voltage source with two-wire current sensing.");
        Keithley2450Limits.ValidateConfiguration(source.ToSetpoint(), source.Sense);
        if (maximumConnectedResistance.Unit != PhysicalUnit.Ohm || maximumConnectedResistance.Value <= 0 || maximumConnectedResistance.Value > 1e15) throw new ArgumentException("Connected threshold must be positive Ohm, at most 1e15.");
        ExperimentDurations.Validate(settle);
        if (route is not null && (route.Device == source.Device || route.Closed.Count != 2 || route.Closed.Select(p => (p.Slot, p.Row)).Distinct().Count() != 2 || route.Closed.Select(p => (p.Slot, p.Column)).Distinct().Count() != 2)) throw new ArgumentException("Continuity requires two distinct matrix Force contacts.");
        Source = source; Channel = channel; Pair = pair; RequiredRoute = route; Settle = settle; MaximumConnectedResistance = maximumConnectedResistance;
        StatusColumn = statusColumn; ReasonColumn = reasonColumn; VoltageColumn = voltageColumn; CurrentColumn = currentColumn; ResistanceColumn = resistanceColumn;
    }
    public SourcePreparation Source { get; }
    public ChannelProfileId Channel { get; }
    public ElectrodePair Pair { get; }
    public MatrixRouteExpectation? RequiredRoute { get; }
    public TimeSpan Settle { get; }
    public Quantity MaximumConnectedResistance { get; }
    public ParameterId StatusColumn { get; }
    public ParameterId ReasonColumn { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public ParameterId ResistanceColumn { get; }
    public IReadOnlyList<ParameterId> Columns => new[] { StatusColumn, ReasonColumn, VoltageColumn, CurrentColumn, ResistanceColumn };
}

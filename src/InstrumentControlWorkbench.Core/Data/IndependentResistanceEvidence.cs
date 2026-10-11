using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

// Separate source/meter observations are not one simultaneous source-sense frame.
public sealed record IndependentResistanceEvidence
{
    public IndependentResistanceEvidence(ReadCurrentResistanceStep acquisition, SourceSetpoint sourceConfiguration,
        Quantity current, Quantity voltage, DateTimeOffset sourceCapturedAt, DateTimeOffset voltageCapturedAt,
        DateTimeOffset sourceConfirmedBefore, DateTimeOffset sourceConfirmedAfter)
    {
        ArgumentNullException.ThrowIfNull(acquisition); Keithley2450Limits.Validate(sourceConfiguration);
        if (sourceConfiguration.Function != SourceFunction.Current || current.Unit != PhysicalUnit.Ampere || voltage.Unit != PhysicalUnit.Volt || Math.Abs(current.Value) <= acquisition.MinimumCurrent.Value)
            throw new ArgumentException("Independent resistance requires current excitation, voltage input and defined V/I.");
        if (acquisition.CurrentReference == CurrentReference.ProgrammedSetpoint && current.Value != sourceConfiguration.Level)
            throw new ArgumentException("Programmed current must equal the confirmed source setpoint.");
        if (sourceConfirmedBefore > sourceCapturedAt || sourceCapturedAt > voltageCapturedAt || voltageCapturedAt > sourceConfirmedAfter)
            throw new ArgumentException("Source and meter capture times must lie in the ordered source confirmation interval.");
        Step = acquisition.Id; Source = acquisition.Source; Meter = acquisition.Meter; CurrentReference = acquisition.CurrentReference;
        VoltageColumn = acquisition.VoltageColumn; CurrentColumn = acquisition.CurrentColumn; ResistanceColumn = acquisition.ResistanceColumn;
        MinimumCurrent = acquisition.MinimumCurrent; Route = acquisition.RequiredRoute; SourceConfiguration = sourceConfiguration;
        Current = current; Voltage = voltage; Resistance = new(voltage.Value / current.Value, PhysicalUnit.Ohm);
        SourceCapturedAt = sourceCapturedAt; VoltageCapturedAt = voltageCapturedAt; SourceConfirmedBefore = sourceConfirmedBefore; SourceConfirmedAfter = sourceConfirmedAfter;
    }
    public StepId Step { get; }
    public InstrumentId Source { get; }
    public InstrumentId Meter { get; }
    public CurrentReference CurrentReference { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public ParameterId ResistanceColumn { get; }
    public Quantity MinimumCurrent { get; }
    public MatrixRouteExpectation? Route { get; }
    public SourceSetpoint SourceConfiguration { get; }
    public Quantity Current { get; }
    public Quantity Voltage { get; }
    public Quantity Resistance { get; }
    public DateTimeOffset SourceCapturedAt { get; }
    public DateTimeOffset VoltageCapturedAt { get; }
    public DateTimeOffset SourceConfirmedBefore { get; }
    public DateTimeOffset SourceConfirmedAfter { get; }
}

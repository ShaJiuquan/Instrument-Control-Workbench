using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public enum CurrentSourceModel { Keithley2450, Keithley6221 }

// Provenance boundary selects the actual model's constraints, never a fake SMU state.
public sealed record CurrentExcitationConfiguration
{
    public CurrentExcitationConfiguration(CurrentSourceModel model, Quantity current, Quantity compliance, Quantity? manualRange = null, Quantity? sourceDelay = null)
    {
        if (model == CurrentSourceModel.Keithley6221) _ = new Keithley6221Settings(current, compliance, manualRange, sourceDelay);
        else if (model == CurrentSourceModel.Keithley2450)
        {
            if (sourceDelay is not null) throw new ArgumentException("K6221 source delay cannot describe a K2450.");
            if (current.Unit != PhysicalUnit.Ampere || compliance.Unit != PhysicalUnit.Volt) throw new ArgumentException("Current excitation requires Ampere/Volt units.");
            Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Current, current.Value, compliance.Value, manualRange));
        }
        else throw new ArgumentException("Unsupported current source model.");
        Model = model; Level = current.Value; Compliance = compliance.Value; ManualRange = manualRange; SourceDelay = sourceDelay;
    }
    public CurrentExcitationConfiguration(SourceSetpoint source) : this(CurrentSourceModel.Keithley2450, new(source.Level, PhysicalUnit.Ampere), new(source.Compliance, PhysicalUnit.Volt), source.ManualRange)
    { if (source.Function != SourceFunction.Current) throw new ArgumentException("Current excitation requires a current source."); }
    public CurrentSourceModel Model { get; }
    public double Level { get; }
    public double Compliance { get; }
    public Quantity? ManualRange { get; }
    public Quantity? SourceDelay { get; }
}

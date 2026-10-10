using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Measurements;

public sealed record VoltageResistanceRecipe
{
    public VoltageResistanceRecipe(string name, Quantity voltage, Quantity compliance, SensingMode sensingMode = SensingMode.TwoWire,
        double nplc = 1, TimeSpan? settle = null, Quantity? minimumCurrent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (voltage.Unit != PhysicalUnit.Volt || compliance.Unit != PhysicalUnit.Ampere || Math.Abs(voltage.Value) > 200)
            throw new ArgumentException("Voltage resistance requires a voltage within ±200 V and Ampere compliance.");
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, voltage.Value, compliance.Value));
        var sense = new SenseConfiguration(SenseFunction.Current, sensingMode, nplc, true);
        Keithley2450Limits.Validate(sense);
        var duration = settle ?? TimeSpan.FromSeconds(0.1);
        ExperimentDurations.Validate(duration);
        // Rust f64::EPSILON, not C# double.Epsilon (the minimum subnormal).
        var threshold = minimumCurrent ?? new Quantity(2.220446049250313e-16, PhysicalUnit.Ampere);
        if (threshold.Unit != PhysicalUnit.Ampere || threshold.Value < 0) throw new ArgumentException("Minimum current must be nonnegative Ampere.");
        Name = name; Voltage = voltage; Compliance = compliance; Sense = sense; Settle = duration; MinimumCurrent = threshold;
    }
    public string Name { get; }
    public Quantity Voltage { get; }
    public Quantity Compliance { get; }
    public SenseConfiguration Sense { get; }
    public TimeSpan Settle { get; }
    public Quantity MinimumCurrent { get; }
}

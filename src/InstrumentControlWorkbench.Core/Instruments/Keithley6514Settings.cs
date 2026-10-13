using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

public enum ElectrometerFunction { Current, Voltage, Resistance, Charge }
// Selected nominal range register, not the range used by an autoranged reading.
public sealed record ElectrometerRange(Quantity Span, bool AutoRange);
public sealed record Keithley6514Settings
{
    private static readonly IReadOnlyList<double>[] Spans =
    [
        Array.AsReadOnly(new[] { 2e-11, 2e-10, 2e-9, 2e-8, 2e-7, 2e-6, 2e-5, 2e-4, 2e-3, 2e-2 }),
        Array.AsReadOnly(new[] { 2.0, 20, 200 }),
        Array.AsReadOnly(new[] { 2e3, 2e4, 2e5, 2e6, 2e7, 2e8, 2e9, 2e10, 2e11 }),
        Array.AsReadOnly(new[] { 2e-8, 2e-7, 2e-6, 2e-5 }),
    ];
    public Keithley6514Settings(ElectrometerFunction function, ElectrometerRange current, ElectrometerRange voltage,
        ElectrometerRange resistance, ElectrometerRange charge, double nplc, bool zeroCheck, bool zeroCorrect, bool averagingEnabled, int averagingCount, AveragingMode averagingMode)
    {
        if (!Enum.IsDefined(function)) throw new ArgumentOutOfRangeException(nameof(function));
        foreach (var pair in new[] { (ElectrometerFunction.Current, current), (ElectrometerFunction.Voltage, voltage), (ElectrometerFunction.Resistance, resistance), (ElectrometerFunction.Charge, charge) })
        {
            ArgumentNullException.ThrowIfNull(pair.Item2);
            if (pair.Item2.Span.Unit != Unit(pair.Item1) || !Ranges(pair.Item1).Contains(pair.Item2.Span.Value)) throw new ArgumentException("K6514 requires exact nominal spans with the function's unit.");
        }
        if (!double.IsFinite(nplc) || nplc is < 0.01 or > 10) throw new ArgumentOutOfRangeException(nameof(nplc));
        if (averagingCount is < 1 or > 100 || !Enum.IsDefined(averagingMode)) throw new ArgumentOutOfRangeException(nameof(averagingCount));
        Function = function; Current = current; Voltage = voltage; Resistance = resistance; Charge = charge; Nplc = nplc;
        ZeroCheck = zeroCheck; ZeroCorrect = zeroCorrect; AveragingEnabled = averagingEnabled; AveragingCount = averagingCount; AveragingMode = averagingMode;
    }
    public ElectrometerFunction Function { get; }
    public ElectrometerRange Current { get; }
    public ElectrometerRange Voltage { get; }
    public ElectrometerRange Resistance { get; }
    public ElectrometerRange Charge { get; }
    public double Nplc { get; }
    public bool ZeroCheck { get; }
    public bool ZeroCorrect { get; }
    public bool AveragingEnabled { get; }
    public int AveragingCount { get; }
    public AveragingMode AveragingMode { get; }
    public ElectrometerRange Range(ElectrometerFunction function) => function switch { ElectrometerFunction.Current => Current, ElectrometerFunction.Voltage => Voltage, ElectrometerFunction.Resistance => Resistance, ElectrometerFunction.Charge => Charge, _ => throw new ArgumentOutOfRangeException(nameof(function)) };
    public static PhysicalUnit Unit(ElectrometerFunction function) => function switch { ElectrometerFunction.Current => PhysicalUnit.Ampere, ElectrometerFunction.Voltage => PhysicalUnit.Volt, ElectrometerFunction.Resistance => PhysicalUnit.Ohm, ElectrometerFunction.Charge => PhysicalUnit.Coulomb, _ => throw new ArgumentOutOfRangeException(nameof(function)) };
    public static IReadOnlyList<double> Ranges(ElectrometerFunction function) => Enum.IsDefined(function) ? Spans[(int)function] : throw new ArgumentOutOfRangeException(nameof(function));
    public static Quantity ResolveRange(ElectrometerFunction function, Quantity selector)
    {
        if (selector.Unit != Unit(function) || selector.Value <= 0 || selector.Value > Ranges(function)[^1] * 1.05) throw new ArgumentException("K6514 range selector must be positive and within the function's measurement span.");
        return new(Ranges(function).FirstOrDefault(s => s >= selector.Value, Ranges(function)[^1]), Unit(function));
    }
    public void ValidateReading(ElectrometerFunction function, Quantity value)
    {
        var range = Range(function);
        if (function != Function || ZeroCheck || value.Unit != Unit(function) || function == ElectrometerFunction.Resistance && value.Value < 0 ||
            Math.Abs(value.Value) > (range.AutoRange ? Ranges(function)[^1] : range.Span.Value) * 1.05)
            throw new ArgumentException("Electrometer reading requires the selected function, zero check off and observed range/unit boundaries.");
    }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureParameters()
    {
        var values = new Dictionary<ParameterId, Quantity>
        {
            [Keithley6514Parameters.Function.Id] = new((int)Function, PhysicalUnit.Dimensionless),
            [Keithley6514Parameters.Nplc.Id] = new(Nplc, PhysicalUnit.Dimensionless),
            [Keithley6514Parameters.ZeroCheck.Id] = new(ZeroCheck ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6514Parameters.ZeroCorrect.Id] = new(ZeroCorrect ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6514Parameters.AveragingEnabled.Id] = new(AveragingEnabled ? 1 : 0, PhysicalUnit.Dimensionless),
            [Keithley6514Parameters.AveragingCount.Id] = new(AveragingCount, PhysicalUnit.Dimensionless),
            [Keithley6514Parameters.AveragingMode.Id] = new((int)AveragingMode, PhysicalUnit.Dimensionless),
        };
        foreach (var function in Enum.GetValues<ElectrometerFunction>())
        {
            values[Keithley6514Parameters.Range(function).Id] = Range(function).Span;
            values[Keithley6514Parameters.AutoRange(function).Id] = new(Range(function).AutoRange ? 1 : 0, PhysicalUnit.Dimensionless);
        }
        return values;
    }
    public Keithley6514Settings SelectFunction(ElectrometerFunction function)
    {
        if (function != Function && !ZeroCheck) throw new InvalidOperationException("Enable zero check explicitly before changing electrometer function; this discharges accumulated charge.");
        return new(function, Current, Voltage, Resistance, Charge, Nplc, ZeroCheck, ZeroCorrect, AveragingEnabled, AveragingCount, AveragingMode);
    }
    public Keithley6514Settings WithParameter(ParameterId parameter, Quantity value)
    {
        Keithley6514Parameters.ValidateWrite(parameter, value);
        if (parameter == Keithley6514Parameters.Function.Id) throw new InvalidOperationException("Select and observe the new function through the port.");
        ElectrometerRange Updated(ElectrometerFunction function)
        {
            var old = Range(function);
            if (parameter == Keithley6514Parameters.Range(function).Id) return new(value, false);
            if (parameter == Keithley6514Parameters.RangeSelector(function).Id) return new(ResolveRange(function, value), false);
            return parameter == Keithley6514Parameters.AutoRange(function).Id ? new(old.Span, value.Value == 1) : old;
        }
        return new(Function, Updated(ElectrometerFunction.Current), Updated(ElectrometerFunction.Voltage), Updated(ElectrometerFunction.Resistance), Updated(ElectrometerFunction.Charge),
            parameter == Keithley6514Parameters.Nplc.Id ? value.Value : Nplc,
            parameter == Keithley6514Parameters.ZeroCheck.Id ? value.Value == 1 : ZeroCheck,
            parameter == Keithley6514Parameters.ZeroCorrect.Id ? value.Value == 1 : ZeroCorrect,
            parameter == Keithley6514Parameters.AveragingEnabled.Id ? value.Value == 1 : AveragingEnabled,
            parameter == Keithley6514Parameters.AveragingCount.Id ? (int)value.Value : AveragingCount,
            parameter == Keithley6514Parameters.AveragingMode.Id ? (AveragingMode)(int)value.Value : AveragingMode);
    }
}

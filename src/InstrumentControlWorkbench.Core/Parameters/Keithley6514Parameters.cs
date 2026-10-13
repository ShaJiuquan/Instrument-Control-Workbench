using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public static class Keithley6514Parameters
{
    public static readonly ParameterDescriptor Function = Control("function", "Electrometer function", 0, 3);
    public static readonly ParameterDescriptor Nplc = Control("nplc", "Shared integration (PLC)", 0.01, 10);
    public static readonly ParameterDescriptor ZeroCheck = Control("zero_check", "Zero check enabled", 0, 1);
    public static readonly ParameterDescriptor ZeroCorrect = Control("zero_correct", "Zero correct enabled (not calibration)", 0, 1);
    public static readonly ParameterDescriptor AveragingEnabled = Control("averaging_enabled", "Shared averaging enabled", 0, 1);
    public static readonly ParameterDescriptor AveragingCount = Control("averaging_count", "Shared averaging count", 1, 100);
    public static readonly ParameterDescriptor AveragingMode = Control("averaging_mode", "Shared averaging mode", 0, 1);
    private static readonly ParameterDescriptor[] Readings = Enum.GetValues<ElectrometerFunction>().Select(f => new ParameterDescriptor(new("k6514.measured_" + Name(f)), "Measured " + Name(f), Keithley6514Settings.Unit(f), ParameterAccess.Read,
        new(f == ElectrometerFunction.Resistance ? 0 : -Keithley6514Settings.Ranges(f)[^1] * 1.05, Keithley6514Settings.Unit(f)), new(Keithley6514Settings.Ranges(f)[^1] * 1.05, Keithley6514Settings.Unit(f)))).ToArray();
    private static readonly ParameterDescriptor[] RangeRegisters = Enum.GetValues<ElectrometerFunction>().Select(f => new ParameterDescriptor(new("k6514." + Name(f) + "_range"), "Selected " + Name(f) + " span register", Keithley6514Settings.Unit(f), ParameterAccess.ReadWrite, new(Keithley6514Settings.Ranges(f)[0], Keithley6514Settings.Unit(f)), new(Keithley6514Settings.Ranges(f)[^1], Keithley6514Settings.Unit(f)))).ToArray();
    private static readonly ParameterDescriptor[] AutoRegisters = Enum.GetValues<ElectrometerFunction>().Select(f => Control(Name(f) + "_auto_range", Name(f) + " autorange", 0, 1)).ToArray();
    private static readonly ParameterDescriptor[] Selectors = Enum.GetValues<ElectrometerFunction>().Select(f => new ParameterDescriptor(new("k6514." + Name(f) + "_select_range"), "Select " + Name(f) + " range for expected reading", Keithley6514Settings.Unit(f), ParameterAccess.Write, new(double.Epsilon, Keithley6514Settings.Unit(f)), new(Keithley6514Settings.Ranges(f)[^1] * 1.05, Keithley6514Settings.Unit(f)))).ToArray();
    public static IReadOnlyList<ParameterDescriptor> All { get; } = Array.AsReadOnly(new[] { Function, Nplc, ZeroCheck, ZeroCorrect, AveragingEnabled, AveragingCount, AveragingMode }.Concat(Readings).Concat(RangeRegisters).Concat(AutoRegisters).Concat(Selectors).ToArray());
    private static ParameterDescriptor Control(string id, string name, double min, double max) => new(new("k6514." + id), name, PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(min, PhysicalUnit.Dimensionless), new(max, PhysicalUnit.Dimensionless));
    private static string Name(ElectrometerFunction function) => function.ToString().ToLowerInvariant();
    public static ParameterDescriptor Reading(ElectrometerFunction function) => Readings[(int)Checked(function)];
    public static ParameterDescriptor Range(ElectrometerFunction function) => RangeRegisters[(int)Checked(function)];
    public static ParameterDescriptor AutoRange(ElectrometerFunction function) => AutoRegisters[(int)Checked(function)];
    public static ParameterDescriptor RangeSelector(ElectrometerFunction function) => Selectors[(int)Checked(function)];
    private static ElectrometerFunction Checked(ElectrometerFunction function) => Enum.IsDefined(function) ? function : throw new ArgumentOutOfRangeException(nameof(function));
    public static ElectrometerFunction? ReadingFunction(ParameterId parameter)
    { foreach (var f in Enum.GetValues<ElectrometerFunction>()) if (Reading(f).Id == parameter) return f; return null; }
    public static void ValidateWrite(ParameterId parameter, Quantity value)
    {
        var descriptor = All.SingleOrDefault(p => p.Id == parameter) ?? throw new NotSupportedException($"Unknown K6514 parameter {parameter}."); descriptor.ValidateWrite(value);
        if (descriptor.Unit == PhysicalUnit.Dimensionless && parameter != Nplc.Id && value.Value != Math.Truncate(value.Value)) throw new ArgumentException($"{parameter} requires an integer.");
        foreach (var f in Enum.GetValues<ElectrometerFunction>()) if (parameter == Range(f).Id && !Keithley6514Settings.Ranges(f).Contains(value.Value)) throw new ArgumentException("K6514 native span registers require exact nominal ranges.");
    }
}

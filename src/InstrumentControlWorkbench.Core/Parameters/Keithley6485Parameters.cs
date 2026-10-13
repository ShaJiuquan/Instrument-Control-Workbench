using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public static class Keithley6485Parameters
{
    public static readonly ParameterDescriptor Current = new(new("k6485.measured_current"), "DC current", PhysicalUnit.Ampere, ParameterAccess.Read, new(-0.021, PhysicalUnit.Ampere), new(0.021, PhysicalUnit.Ampere));
    public static readonly ParameterDescriptor AutoRange = Control("auto_range", "Autorange enabled", ParameterAccess.Read, 0, 1);
    public static readonly ParameterDescriptor EnableAutoRange = Control("enable_auto_range", "Enable autorange", ParameterAccess.Write, 1, 1);
    public static readonly ParameterDescriptor ManualRange = new(new("k6485.manual_range"), "Manual current span", PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(2e-9, PhysicalUnit.Ampere), new(0.02, PhysicalUnit.Ampere));
    public static readonly ParameterDescriptor Nplc = Control("nplc", "Integration (PLC)", ParameterAccess.ReadWrite, 0.01, 60);
    public static readonly ParameterDescriptor ZeroCheck = Control("zero_check", "Zero check enabled", ParameterAccess.ReadWrite, 0, 1);
    public static readonly ParameterDescriptor ZeroCorrect = Control("zero_correct", "Zero correct enabled (not calibration evidence)", ParameterAccess.ReadWrite, 0, 1);
    public static readonly ParameterDescriptor AveragingEnabled = Control("averaging_enabled", "Averaging enabled", ParameterAccess.ReadWrite, 0, 1);
    public static readonly ParameterDescriptor AveragingCount = Control("averaging_count", "Averaging count", ParameterAccess.ReadWrite, 1, 100);
    public static readonly ParameterDescriptor AveragingMode = Control("averaging_mode", "Averaging mode", ParameterAccess.ReadWrite, 0, 1);
    public static IReadOnlyList<ParameterDescriptor> All { get; } = Array.AsReadOnly(new[] { Current, AutoRange, EnableAutoRange, ManualRange, Nplc, ZeroCheck, ZeroCorrect, AveragingEnabled, AveragingCount, AveragingMode });
    private static ParameterDescriptor Control(string id, string name, ParameterAccess access, double min, double max) => new(new("k6485." + id), name, PhysicalUnit.Dimensionless, access, new(min, PhysicalUnit.Dimensionless), new(max, PhysicalUnit.Dimensionless));
    public static void ValidateWrite(ParameterId parameter, Quantity value)
    {
        var descriptor = All.SingleOrDefault(p => p.Id == parameter) ?? throw new NotSupportedException($"Unknown K6485 parameter {parameter}.");
        descriptor.ValidateWrite(value);
        if (parameter != Nplc.Id && parameter != ManualRange.Id && value.Value != Math.Truncate(value.Value)) throw new ArgumentException($"{parameter} requires an integer.");
        if (parameter == ManualRange.Id && !Keithley6485Settings.Ranges.Contains(value.Value)) throw new ArgumentException("K6485 requires an exact nominal current span.");
    }
}

using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public static class Keithley2182Parameters
{
    public static readonly ParameterDescriptor Voltage = new(new("k2182.measured_voltage"), "DC voltage", PhysicalUnit.Volt, ParameterAccess.Read, new(-Keithley2182Settings.MaximumInputVoltage(1), PhysicalUnit.Volt), new(Keithley2182Settings.MaximumInputVoltage(1), PhysicalUnit.Volt));
    public static readonly ParameterDescriptor Channel = new(new("k2182.channel"), "Voltage input", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(1, PhysicalUnit.Dimensionless), new(2, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor AutoRange = new(new("k2182.auto_range"), "Autorange enabled", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor EnableAutoRange = new(new("k2182.enable_auto_range"), "Enable autorange", PhysicalUnit.Dimensionless, ParameterAccess.Write, new(1, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor ManualRange = new(new("k2182.manual_range"), "Manual voltage span", PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(0.01, PhysicalUnit.Volt), new(100, PhysicalUnit.Volt));
    public static readonly ParameterDescriptor RangeSelector = new(new("k2182.select_range"), "Select voltage range for expected reading", PhysicalUnit.Volt, ParameterAccess.Write, new(double.Epsilon, PhysicalUnit.Volt), new(120, PhysicalUnit.Volt));
    public static readonly ParameterDescriptor Nplc = new(new("k2182.nplc"), "Integration (PLC)", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(Keithley2182Settings.MinimumNplc, PhysicalUnit.Dimensionless), new(Keithley2182Settings.MaximumNplc, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor FilterEnabled = new(new("k2182.filter_enabled"), "Digital filter enabled", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor FilterCount = new(new("k2182.filter_count"), "Digital filter count", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(Keithley2182Settings.MinimumFilterCount, PhysicalUnit.Dimensionless), new(Keithley2182Settings.MaximumFilterCount, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor FilterMode = new(new("k2182.filter_mode"), "Digital filter mode", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor FilterWindowPercent = new(new("k2182.filter_window_percent"), "Filter window (% of span)", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(0, PhysicalUnit.Dimensionless), new(Keithley2182Settings.MaximumFilterWindowPercent, PhysicalUnit.Dimensionless));
    public static IReadOnlyList<ParameterDescriptor> All { get; } = Array.AsReadOnly(new[] { Voltage, Channel, AutoRange, EnableAutoRange, ManualRange, RangeSelector, Nplc, FilterEnabled, FilterCount, FilterMode, FilterWindowPercent });
    public static bool IsParameter(ParameterId parameter) => All.Any(p => p.Id == parameter);
    public static void ValidateWrite(ParameterId parameter, Quantity value)
    {
        var descriptor = All.SingleOrDefault(p => p.Id == parameter) ?? throw new NotSupportedException($"Unknown K2182 parameter {parameter}.");
        descriptor.ValidateWrite(value);
        if ((parameter == Channel.Id || parameter == FilterEnabled.Id || parameter == FilterCount.Id || parameter == FilterMode.Id) && value.Value != Math.Truncate(value.Value))
            throw new ArgumentException($"{parameter} requires an integer.");
        if (parameter == ManualRange.Id && !Keithley2182Settings.Ranges(1).Contains(value.Value))
            throw new ArgumentException("K2182 native manual ranges must be exact nominal spans.");
    }
}

using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// DC output only. No sensing configuration or measured-current capability.
public sealed record Keithley6221Settings
{
    public static IReadOnlyList<double> NominalRanges { get; } = Array.AsReadOnly(new[] { 2e-9, 20e-9, 200e-9, 2e-6, 20e-6, 200e-6, 2e-3, 20e-3, 0.1 });
    public Keithley6221Settings(Quantity current, Quantity compliance, Quantity? manualRange = null)
    {
        if (current.Unit != PhysicalUnit.Ampere || Math.Abs(current.Value) > 0.105 || compliance.Unit != PhysicalUnit.Volt || compliance.Value is < 0.1 or > 105)
            throw new ArgumentException("K6221 DC current requires ±105 mA and 0.1..105 V compliance.");
        if (manualRange is { } span && (span.Unit != PhysicalUnit.Ampere || !NominalRanges.Contains(span.Value) || Math.Abs(current.Value) > span.Value * 1.05))
            throw new ArgumentException("K6221 fixed range must be nominal and contain the current within 5% overrange.");
        Current = current; Compliance = compliance; ManualRange = manualRange;
    }
    public Quantity Current { get; }
    public Quantity Compliance { get; }
    public Quantity? ManualRange { get; }
    public static Quantity ResolveRange(Quantity selector)
    {
        if (selector.Unit != PhysicalUnit.Ampere || selector.Value is <= 0 or > 0.105) throw new ArgumentException("K6221 range selector requires positive Ampere within 105 mA.");
        return new(NominalRanges.First(r => r >= selector.Value || r == NominalRanges[^1] && selector.Value <= r * 1.05), PhysicalUnit.Ampere);
    }
    public Keithley6221Settings WithParameter(ParameterId parameter, Quantity value)
    {
        Keithley6221Parameters.ValidateWrite(parameter, value);
        return parameter == Keithley6221Parameters.Current.Id ? new(value, Compliance, ManualRange)
            : parameter == Keithley6221Parameters.Compliance.Id ? new(Current, value, ManualRange)
            : parameter == Keithley6221Parameters.ManualRange.Id ? new(Current, Compliance, value)
            : parameter == Keithley6221Parameters.EnableAutoRange.Id ? new(Current, Compliance)
            : throw new ArgumentException("Parameter does not configure K6221 DC settings.");
    }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureParameters()
    {
        var values = new Dictionary<ParameterId, Quantity>
        {
            [Keithley6221Parameters.Current.Id] = Current,
            [Keithley6221Parameters.Compliance.Id] = Compliance,
            [Keithley6221Parameters.AutoRange.Id] = new(ManualRange is null ? 1 : 0, PhysicalUnit.Dimensionless)
        };
        if (ManualRange is { } span) values.Add(Keithley6221Parameters.ManualRange.Id, span);
        return values;
    }
}
public static class Keithley6221Parameters
{
    public static readonly ParameterDescriptor Current = new(new("k6221.source_current"), "Programmed current", PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(-0.105, PhysicalUnit.Ampere), new(0.105, PhysicalUnit.Ampere));
    public static readonly ParameterDescriptor Compliance = new(new("k6221.voltage_compliance"), "Voltage compliance", PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(0.1, PhysicalUnit.Volt), new(105, PhysicalUnit.Volt));
    public static readonly ParameterDescriptor Output = new(new("k6221.output_enabled"), "Output enabled", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor AutoRange = new(new("k6221.source_autorange"), "Source autorange", PhysicalUnit.Dimensionless, ParameterAccess.Read);
    public static readonly ParameterDescriptor EnableAutoRange = new(new("k6221.enable_source_autorange"), "Enable source autorange", PhysicalUnit.Dimensionless, ParameterAccess.Write, new(1, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor ManualRange = new(new("k6221.source_range"), "Manual current range", PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(2e-9, PhysicalUnit.Ampere), new(0.1, PhysicalUnit.Ampere));
    public static IReadOnlyList<ParameterDescriptor> All { get; } = Array.AsReadOnly(new[] { Current, Compliance, Output, AutoRange, EnableAutoRange, ManualRange });
    public static void ValidateWrite(ParameterId parameter, Quantity value)
    {
        var descriptor = All.Single(p => p.Id == parameter); descriptor.ValidateWrite(value);
        if (parameter == Output.Id && value.Value != Math.Truncate(value.Value) || parameter == ManualRange.Id && !Keithley6221Settings.NominalRanges.Contains(value.Value))
            throw new ArgumentException("K6221 output must be 0/1 and manual range must be nominal.");
    }
}

namespace InstrumentControlWorkbench.Core.Parameters;

public static class Keithley2450Parameters
{
    public static ParameterDescriptor SourceVoltage { get; } = new(new("k2450.source_voltage"), "Source voltage",
        PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(-210, PhysicalUnit.Volt), new(210, PhysicalUnit.Volt));
    public static ParameterDescriptor SourceCurrent { get; } = new(new("k2450.source_current"), "Source current",
        PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(-1.05, PhysicalUnit.Ampere), new(1.05, PhysicalUnit.Ampere));
    public static ParameterDescriptor CurrentCompliance { get; } = new(new("k2450.current_compliance"), "Current compliance",
        PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(1e-9, PhysicalUnit.Ampere), new(1.05, PhysicalUnit.Ampere));
    public static ParameterDescriptor VoltageCompliance { get; } = new(new("k2450.voltage_compliance"), "Voltage compliance",
        PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(0.02, PhysicalUnit.Volt), new(210, PhysicalUnit.Volt));
    public static ParameterDescriptor MeasuredCurrent { get; } = new(new("k2450.measured_current"), "Measured current", PhysicalUnit.Ampere, ParameterAccess.Read);
    public static ParameterDescriptor MeasuredVoltage { get; } = new(new("k2450.measured_voltage"), "Measured voltage", PhysicalUnit.Volt, ParameterAccess.Read);
    public static ParameterDescriptor MeasuredResistance { get; } = new(new("k2450.measured_resistance"), "Measured resistance", PhysicalUnit.Ohm, ParameterAccess.Read);
    public static ParameterDescriptor Nplc { get; } = new(new("k2450.nplc"), "Integration time (line cycles)",
        PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(0.01, PhysicalUnit.Dimensionless), new(10, PhysicalUnit.Dimensionless));
}

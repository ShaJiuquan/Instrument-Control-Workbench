using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public static class Keithley2450Parameters
{
    public static ParameterDescriptor ManualSenseVoltageRange { get; } = new(new("k2450.manual_sense_voltage_range"), "Manual sense voltage range", PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(0.02, PhysicalUnit.Volt), new(200, PhysicalUnit.Volt));
    public static ParameterDescriptor ManualSenseCurrentRange { get; } = new(new("k2450.manual_sense_current_range"), "Manual sense current range", PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(1e-8, PhysicalUnit.Ampere), new(1, PhysicalUnit.Ampere));
    public static ParameterDescriptor ManualSenseResistanceRange { get; } = new(new("k2450.manual_sense_resistance_range"), "Observed manual resistance range", PhysicalUnit.Ohm, ParameterAccess.Read, new(20, PhysicalUnit.Ohm), new(2e8, PhysicalUnit.Ohm));
    public static ParameterDescriptor SenseAutoRange { get; } = new(new("k2450.sense_auto_range"), "Automatic sense range (1 = enabled)", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static ParameterDescriptor EnableSenseAutoRange { get; } = new(new("k2450.enable_sense_auto_range"), "Enable automatic sense range", PhysicalUnit.Dimensionless, ParameterAccess.Write, new(1, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static ParameterDescriptor ManualSenseRange(SenseFunction function) => function switch
    {
        SenseFunction.Voltage => ManualSenseVoltageRange,
        SenseFunction.Current => ManualSenseCurrentRange,
        SenseFunction.Resistance => ManualSenseResistanceRange,
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };
    public static ParameterDescriptor ManualVoltageRange { get; } = new(new("k2450.manual_voltage_range"), "Manual source voltage range", PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(0.02, PhysicalUnit.Volt), new(200, PhysicalUnit.Volt));
    public static ParameterDescriptor ManualCurrentRange { get; } = new(new("k2450.manual_current_range"), "Manual source current range", PhysicalUnit.Ampere, ParameterAccess.ReadWrite, new(1e-8, PhysicalUnit.Ampere), new(1, PhysicalUnit.Ampere));
    public static ParameterDescriptor SourceAutoRange { get; } = new(new("k2450.source_auto_range"), "Automatic source range (1 = enabled)", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static ParameterDescriptor EnableSourceAutoRange { get; } = new(new("k2450.enable_source_auto_range"), "Enable automatic source range", PhysicalUnit.Dimensionless, ParameterAccess.Write, new(1, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
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

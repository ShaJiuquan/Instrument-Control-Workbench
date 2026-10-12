using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public static class Sr830Parameters
{
    public static readonly ParameterDescriptor X = new(new("sr830.raw_x_voltage"), "Raw X voltage", PhysicalUnit.Volt, ParameterAccess.Read);
    public static readonly ParameterDescriptor Y = new(new("sr830.raw_y_voltage"), "Raw Y voltage", PhysicalUnit.Volt, ParameterAccess.Read);
    public static readonly ParameterDescriptor R = new(new("sr830.raw_r_voltage"), "Raw magnitude voltage", PhysicalUnit.Volt, ParameterAccess.Read, new(0, PhysicalUnit.Volt));
    public static readonly ParameterDescriptor Theta = new(new("sr830.theta"), "Phase", PhysicalUnit.Degree, ParameterAccess.Read, new(-180, PhysicalUnit.Degree), new(180, PhysicalUnit.Degree));
    public static readonly ParameterDescriptor InputMode = new(new("sr830.input_mode"), "Input mode code", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(0, PhysicalUnit.Dimensionless), new(3, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor Sensitivity = new(new("sr830.sensitivity_register"), "Sensitivity register (voltage scale)", PhysicalUnit.Volt, ParameterAccess.ReadWrite, new(2e-9, PhysicalUnit.Volt), new(1, PhysicalUnit.Volt));
    public static readonly ParameterDescriptor TimeConstant = new(new("sr830.time_constant"), "Time constant", PhysicalUnit.Second, ParameterAccess.ReadWrite, new(1e-5, PhysicalUnit.Second), new(30000, PhysicalUnit.Second));
    public static readonly ParameterDescriptor FilterSlope = new(new("sr830.filter_slope"), "Filter slope (dB/oct)", PhysicalUnit.Dimensionless, ParameterAccess.ReadWrite, new(6, PhysicalUnit.Dimensionless), new(24, PhysicalUnit.Dimensionless));
    public static IReadOnlyList<ParameterDescriptor> All { get; } = Array.AsReadOnly(new[] { X, Y, R, Theta, InputMode, Sensitivity, TimeConstant, FilterSlope });
    public static void ValidateWrite(ParameterId parameter, Quantity value)
    {
        var descriptor = All.SingleOrDefault(p => p.Id == parameter) ?? throw new NotSupportedException($"Unknown SR830 parameter {parameter}.");
        descriptor.ValidateWrite(value);
        if (parameter == InputMode.Id && value.Value != Math.Truncate(value.Value) || parameter == FilterSlope.Id && value.Value is not (6 or 12 or 18 or 24)) throw new ArgumentException("SR830 input/slope requires a supported discrete code.");
        if (parameter == Sensitivity.Id && !Sr830InputSettings.Sensitivities.Contains(value.Value) || parameter == TimeConstant.Id && !Sr830InputSettings.TimeConstants.Contains(value.Value)) throw new ArgumentException("SR830 sensitivity/time constant must be an exact nominal setting.");
    }
}

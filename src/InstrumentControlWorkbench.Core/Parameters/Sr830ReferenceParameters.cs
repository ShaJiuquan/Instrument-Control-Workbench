namespace InstrumentControlWorkbench.Core.Parameters;

public static class Sr830ReferenceParameters
{
    public static readonly ParameterDescriptor Source = new(new("sr830.reference_source"), "Reference source code", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless));
    public static readonly ParameterDescriptor Frequency = new(new("sr830.reference_frequency"), "Observed reference frequency", PhysicalUnit.Hertz, ParameterAccess.Read, new(.001, PhysicalUnit.Hertz), new(102000, PhysicalUnit.Hertz));
    public static readonly ParameterDescriptor SineAmplitude = new(new("sr830.sine_amplitude_readback"), "Observed sine amplitude (instrument)", PhysicalUnit.Volt, ParameterAccess.Read, new(.004, PhysicalUnit.Volt), new(5, PhysicalUnit.Volt));
    public static readonly ParameterDescriptor Harmonic = new(new("sr830.detection_harmonic"), "Detection harmonic", PhysicalUnit.Dimensionless, ParameterAccess.Read, new(1, PhysicalUnit.Dimensionless), new(19999, PhysicalUnit.Dimensionless));
    public static IReadOnlyList<ParameterDescriptor> All { get; } = Array.AsReadOnly(new[] { Source, Frequency, SineAmplitude, Harmonic });
}

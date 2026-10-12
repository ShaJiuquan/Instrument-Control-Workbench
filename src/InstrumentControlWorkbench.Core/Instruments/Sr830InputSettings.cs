using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

public enum Sr830InputMode { VoltageA, VoltageAB, Current1M, Current100M }

// Passive input subset. Oscillator output is deliberately not an input setting.
public sealed record Sr830InputSettings
{
    public static IReadOnlyList<double> Sensitivities { get; } = Array.AsReadOnly(new[] { 2e-9, 5e-9, 1e-8, 2e-8, 5e-8, 1e-7, 2e-7, 5e-7, 1e-6, 2e-6, 5e-6, 1e-5, 2e-5, 5e-5, 1e-4, 2e-4, 5e-4, 1e-3, 2e-3, 5e-3, 1e-2, 2e-2, 5e-2, 1e-1, 2e-1, 5e-1, 1d });
    public static IReadOnlyList<double> TimeConstants { get; } = Array.AsReadOnly(new[] { 1e-5, 3e-5, 1e-4, 3e-4, 1e-3, 3e-3, 1e-2, 3e-2, 1e-1, 3e-1, 1d, 3d, 10d, 30d, 100d, 300d, 1000d, 3000d, 10000d, 30000d });
    public Sr830InputSettings(Sr830InputMode input, Quantity sensitivity, Quantity timeConstant, int filterSlope)
    {
        if (!Enum.IsDefined(input)) throw new ArgumentOutOfRangeException(nameof(input));
        if (sensitivity.Unit != PhysicalUnit.Volt || !Sensitivities.Contains(sensitivity.Value)) throw new ArgumentException("SR830 sensitivity requires an exact nominal voltage-scale register value.");
        if (timeConstant.Unit != PhysicalUnit.Second || !TimeConstants.Contains(timeConstant.Value)) throw new ArgumentException("SR830 time constant requires an exact nominal value in seconds.");
        if (filterSlope is not (6 or 12 or 18 or 24)) throw new ArgumentOutOfRangeException(nameof(filterSlope));
        // Above 10 nA (SENS register > .01 on the voltage scale), the device
        // automatically selects 1 MOhm; do not claim a stable 100 MOhm profile.
        if (input == Sr830InputMode.Current100M && sensitivity.Value > .01) throw new ArgumentException("SR830 100 MOhm input cannot retain sensitivity registers above .01.");
        Input = input; Sensitivity = sensitivity; TimeConstant = timeConstant; FilterSlope = filterSlope;
    }
    public Sr830InputMode Input { get; }
    public Quantity Sensitivity { get; }
    public Quantity TimeConstant { get; }
    public int FilterSlope { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureParameters() => new Dictionary<ParameterId, Quantity>
    {
        [Sr830Parameters.InputMode.Id] = new((int)Input, PhysicalUnit.Dimensionless),
        [Sr830Parameters.Sensitivity.Id] = Sensitivity,
        [Sr830Parameters.TimeConstant.Id] = TimeConstant,
        [Sr830Parameters.FilterSlope.Id] = new(FilterSlope, PhysicalUnit.Dimensionless),
    };
    public Sr830InputSettings WithParameter(ParameterId parameter, Quantity value)
    {
        Sr830Parameters.ValidateWrite(parameter, value);
        return new(parameter == Sr830Parameters.InputMode.Id ? (Sr830InputMode)(int)value.Value : Input,
            parameter == Sr830Parameters.Sensitivity.Id ? value : Sensitivity,
            parameter == Sr830Parameters.TimeConstant.Id ? value : TimeConstant,
            parameter == Sr830Parameters.FilterSlope.Id ? (int)value.Value : FilterSlope);
    }
}

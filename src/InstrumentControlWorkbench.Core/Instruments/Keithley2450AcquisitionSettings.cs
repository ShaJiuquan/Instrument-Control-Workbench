using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

public enum AveragingMode { Repeat, Moving }

// An optional preparation profile is a request; a non-null snapshot profile is
// observed configuration (or explicitly simulated state), not reading quality.
public sealed record Keithley2450AcquisitionSettings
{
    public Keithley2450AcquisitionSettings(bool autoZero, bool autorangeRebound, bool averagingEnabled, int averagingCount, AveragingMode averagingMode)
    {
        if (averagingCount is < 1 or > 100 || !Enum.IsDefined(averagingMode)) throw new ArgumentOutOfRangeException(nameof(averagingCount));
        AutoZero = autoZero; AutorangeRebound = autorangeRebound; AveragingEnabled = averagingEnabled; AveragingCount = averagingCount; AveragingMode = averagingMode;
    }
    public bool AutoZero { get; }
    public bool AutorangeRebound { get; }
    public bool AveragingEnabled { get; }
    public int AveragingCount { get; }
    public AveragingMode AveragingMode { get; }

    public IEnumerable<ParameterSnapshot> CaptureParameters(SenseFunction function)
    {
        yield return new(Keithley2450Parameters.AutoZero, new(AutoZero ? 1 : 0, PhysicalUnit.Dimensionless));
        yield return new(Keithley2450Parameters.AveragingEnabled, new(AveragingEnabled ? 1 : 0, PhysicalUnit.Dimensionless));
        yield return new(Keithley2450Parameters.AveragingCount, new(AveragingCount, PhysicalUnit.Dimensionless));
        yield return new(Keithley2450Parameters.AveragingMode, new((int)AveragingMode, PhysicalUnit.Dimensionless));
        if (function != SenseFunction.Resistance) yield return new(Keithley2450Parameters.AutorangeRebound, new(AutorangeRebound ? 1 : 0, PhysicalUnit.Dimensionless));
    }

    public static bool IsParameter(ParameterId parameter) => parameter == Keithley2450Parameters.AutoZero.Id || parameter == Keithley2450Parameters.AutorangeRebound.Id ||
        parameter == Keithley2450Parameters.AveragingEnabled.Id || parameter == Keithley2450Parameters.AveragingCount.Id || parameter == Keithley2450Parameters.AveragingMode.Id;

    public static void ValidateParameter(ParameterId parameter, Quantity value)
    {
        if (!IsParameter(parameter)) throw new ArgumentException("Not an acquisition setting.", nameof(parameter));
        if (value.Unit != PhysicalUnit.Dimensionless || value.Value != Math.Truncate(value.Value) ||
            (parameter == Keithley2450Parameters.AveragingCount.Id ? value.Value is < 1 or > 100 : value.Value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(value), "Acquisition controls require integer counts 1..100 or Boolean/mode codes 0..1.");
    }
    public Keithley2450AcquisitionSettings WithParameter(ParameterId parameter, Quantity value)
    {
        ValidateParameter(parameter, value);
        return new(parameter == Keithley2450Parameters.AutoZero.Id ? value.Value == 1 : AutoZero,
            parameter == Keithley2450Parameters.AutorangeRebound.Id ? value.Value == 1 : AutorangeRebound,
            parameter == Keithley2450Parameters.AveragingEnabled.Id ? value.Value == 1 : AveragingEnabled,
            parameter == Keithley2450Parameters.AveragingCount.Id ? (int)value.Value : AveragingCount,
            parameter == Keithley2450Parameters.AveragingMode.Id ? (AveragingMode)(int)value.Value : AveragingMode);
    }
}

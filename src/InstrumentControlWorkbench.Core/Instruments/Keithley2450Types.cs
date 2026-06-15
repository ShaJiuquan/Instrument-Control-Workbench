namespace InstrumentControlWorkbench.Core.Instruments;

public enum SourceFunction
{
    Voltage,
    Current,
}

public enum SenseFunction
{
    Voltage,
    Current,
    Resistance,
}

public enum SensingMode
{
    TwoWire,
    FourWire,
}

public enum Terminals
{
    Front,
    Rear,
}

public sealed record SourceSetpoint(SourceFunction Function, double Level, double Compliance)
{
    public string LevelUnit => Function == SourceFunction.Voltage ? "V" : "A";
    public string ComplianceUnit => Function == SourceFunction.Voltage ? "A" : "V";
}

public sealed record SenseConfiguration(
    SenseFunction Function,
    SensingMode Mode,
    double Nplc,
    bool AutoRange);

public sealed record MeasurementValue(double Value, string Unit, DateTimeOffset Timestamp);

public sealed record SourceMeterSnapshot(
    InstrumentId InstrumentId,
    string Identifier,
    InstrumentLifecycleState Lifecycle,
    bool Connected,
    bool OutputEnabled,
    Terminals Terminals,
    SourceSetpoint Source,
    SenseConfiguration Sense,
    DateTimeOffset LastHeartbeat);

public static class Keithley2450Limits
{
    public static void Validate(SourceSetpoint setpoint)
    {
        if (!double.IsFinite(setpoint.Level))
        {
            throw new ArgumentOutOfRangeException(nameof(setpoint), "Source level must be finite.");
        }

        if (!double.IsFinite(setpoint.Compliance))
        {
            throw new ArgumentOutOfRangeException(nameof(setpoint), "Compliance must be finite.");
        }

        if (setpoint.Function == SourceFunction.Voltage)
        {
            RequireRange(setpoint.Level, -210.0, 210.0, "Source voltage must be within +/-210 V.");
            RequireRange(setpoint.Compliance, 1e-9, 1.05, "Voltage source current limit must be 1 nA to 1.05 A.");
        }
        else
        {
            RequireRange(setpoint.Level, -1.05, 1.05, "Source current must be within +/-1.05 A.");
            RequireRange(setpoint.Compliance, 0.02, 210.0, "Current source voltage limit must be 0.02 V to 210 V.");
        }
    }

    public static void Validate(SenseConfiguration sense)
    {
        RequireRange(sense.Nplc, 0.01, 10.0, "NPLC must be 0.01 to 10.");
    }

    private static void RequireRange(double value, double min, double max, string message)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(nameof(value), message);
        }
    }
}

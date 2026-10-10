using InstrumentControlWorkbench.Core.Parameters;

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
        ArgumentNullException.ThrowIfNull(setpoint);
        if (!Enum.IsDefined(setpoint.Function)) throw new ArgumentOutOfRangeException(nameof(setpoint), "Unknown source function.");
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
            Keithley2450Parameters.SourceVoltage.ValidateWrite(new(setpoint.Level, PhysicalUnit.Volt));
            Keithley2450Parameters.CurrentCompliance.ValidateWrite(new(setpoint.Compliance, PhysicalUnit.Ampere));
        }
        else
        {
            Keithley2450Parameters.SourceCurrent.ValidateWrite(new(setpoint.Level, PhysicalUnit.Ampere));
            Keithley2450Parameters.VoltageCompliance.ValidateWrite(new(setpoint.Compliance, PhysicalUnit.Volt));
        }
    }

    public static void Validate(SenseConfiguration sense)
    {
        ArgumentNullException.ThrowIfNull(sense);
        if (!Enum.IsDefined(sense.Function) || !Enum.IsDefined(sense.Mode))
            throw new ArgumentOutOfRangeException(nameof(sense), "Unknown sense function or wiring mode.");
        Keithley2450Parameters.Nplc.ValidateWrite(new(sense.Nplc, PhysicalUnit.Dimensionless));
    }
}

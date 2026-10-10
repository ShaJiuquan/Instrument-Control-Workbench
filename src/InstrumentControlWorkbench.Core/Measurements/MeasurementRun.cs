namespace InstrumentControlWorkbench.Core.Measurements;

public enum MeasurementRunState
{
    Idle,
    Running,
    Completed,
    Aborted,
    Faulted,
}

public sealed record MeasurementRun(
    string Id,
    string Name,
    MeasurementRunState State,
    DateTimeOffset StartedAt,
    IReadOnlyList<MeasurementPoint> Points)
{
    public MeasurementPoint? LatestPoint => Points.Count == 0 ? null : Points[^1];
}

public sealed record IvSweepRecipe(
    string Name,
    double StartVoltage,
    double StopVoltage,
    int Points,
    double ComplianceAmps,
    double DwellSeconds)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Points is < 2 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(Points), "A sweep needs 2 to 1,000,000 points.");
        if (!double.IsFinite(DwellSeconds) || DwellSeconds < 0 || DwellSeconds > 86_400)
            throw new ArgumentOutOfRangeException(nameof(DwellSeconds), "Dwell must be finite and between 0 and 86,400 seconds.");
        Instruments.Keithley2450Limits.Validate(new Instruments.SourceSetpoint(Instruments.SourceFunction.Voltage, StartVoltage, ComplianceAmps));
        Instruments.Keithley2450Limits.Validate(new Instruments.SourceSetpoint(Instruments.SourceFunction.Voltage, StopVoltage, ComplianceAmps));
    }

    public IEnumerable<double> VoltagePoints()
    {
        if (Points < 2)
        {
            yield return StartVoltage;
            yield break;
        }

        var step = (StopVoltage - StartVoltage) / (Points - 1);
        for (var i = 0; i < Points; i++)
        {
            yield return StartVoltage + step * i;
        }
    }
}

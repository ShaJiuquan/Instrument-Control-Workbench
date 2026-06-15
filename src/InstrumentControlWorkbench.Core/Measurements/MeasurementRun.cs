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

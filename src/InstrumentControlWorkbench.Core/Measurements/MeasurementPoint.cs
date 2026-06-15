namespace InstrumentControlWorkbench.Core.Measurements;

public sealed record MeasurementPoint(
    DateTimeOffset Timestamp,
    TimeSpan Elapsed,
    double SourceValue,
    double MeasuredValue,
    string SourceUnit,
    string MeasuredUnit);

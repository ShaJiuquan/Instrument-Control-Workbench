using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record EnvironmentObservation
{
    public EnvironmentObservation(EnvironmentRole role, ParameterId column, ParameterAddress? source, DateTimeOffset capturedAt, Quantity? value, string? error = null)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(column);
        if (capturedAt == default || (value is null) == (error is null) || error is not null && (string.IsNullOrWhiteSpace(error) || error.Length > 8192))
            throw new ArgumentException("Environment observation needs capture time and either an actual value or a bounded error.");
        if (value is { } actual && (source is null || actual.Unit != EnvironmentRead.Unit(role))) throw new ArgumentException("Actual environment values require the matching unit and bound source.");
        Role = role; Column = column; Source = source; CapturedAt = capturedAt; Value = value; Error = error;
    }
    public EnvironmentRole Role { get; }
    public ParameterId Column { get; }
    public ParameterAddress? Source { get; }
    public DateTimeOffset CapturedAt { get; }
    public Quantity? Value { get; }
    public string? Error { get; }
}

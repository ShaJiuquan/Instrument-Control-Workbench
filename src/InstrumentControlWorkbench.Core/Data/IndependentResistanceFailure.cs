using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

// A terminal acquisition failed, and owned off/managed excitation plus idle
// were freshly confirmed. No electrical quantity or successful quality is implied.
public sealed record IndependentResistanceFailure
{
    public IndependentResistanceFailure(ReadCurrentResistanceStep acquisition, CurrentExcitationConfiguration sourceConfiguration,
        InstrumentReadFailureKind kind, int? commandCode, string message, Keithley2182Settings meterSettings,
        DateTimeOffset sourceConfirmedBefore, DateTimeOffset attemptStartedAt, DateTimeOffset failedAt,
        DateTimeOffset sourceConfirmedAfter, DateTimeOffset idleConfirmedAt, DateTimeOffset? sourceOffConfirmedAt,
        CurrentResistanceRetryEvidence? retry = null)
    {
        ArgumentNullException.ThrowIfNull(acquisition); ArgumentNullException.ThrowIfNull(sourceConfiguration); ArgumentNullException.ThrowIfNull(meterSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (acquisition.Retry is not { ContinueOnAcquisitionFailure: true } policy ||
            !InstrumentReadException.IsRecoverableAcquisition(kind, commandCode) || (kind == InstrumentReadFailureKind.CommandRejected) != (commandCode is not null) || message.Length > 4096 ||
            sourceConfirmedBefore > attemptStartedAt || attemptStartedAt > failedAt || failedAt > sourceConfirmedAfter || sourceConfirmedAfter > idleConfirmedAt ||
            policy.OwnsSourceOutput != (sourceOffConfirmedAt is not null) || sourceOffConfirmedAt is { } off && (off < failedAt || off > idleConfirmedAt) ||
            sourceConfiguration.Model == CurrentSourceModel.Keithley6221 && acquisition.CurrentReference != CurrentReference.ProgrammedSetpoint ||
            Math.Abs(sourceConfiguration.Level) <= acquisition.MinimumCurrent.Value)
            throw new ArgumentException("Invalid resistance requires recoverable terminal acquisition and ordered unchanged excitation/idle proof matching its policy.");
        retry?.Validate(policy, sourceConfirmedBefore);
        if (retry is not null && retry.MeterSettings != meterSettings) throw new ArgumentException("All attempts require unchanged observed meter settings.");
        Step = acquisition.Id; Source = acquisition.Source; Meter = acquisition.Meter; CurrentReference = acquisition.CurrentReference;
        VoltageColumn = acquisition.VoltageColumn; CurrentColumn = acquisition.CurrentColumn; ResistanceColumn = acquisition.ResistanceColumn;
        MinimumCurrent = acquisition.MinimumCurrent; Route = acquisition.RequiredRoute; SourceConfiguration = sourceConfiguration;
        Kind = kind; CommandCode = commandCode; Message = message; MeterSettings = meterSettings;
        SourceConfirmedBefore = sourceConfirmedBefore; AttemptStartedAt = attemptStartedAt; FailedAt = failedAt;
        SourceConfirmedAfter = sourceConfirmedAfter; IdleConfirmedAt = idleConfirmedAt; SourceOffConfirmedAt = sourceOffConfirmedAt; Retry = retry;
    }
    public StepId Step { get; }
    public InstrumentId Source { get; }
    public InstrumentId Meter { get; }
    public CurrentReference CurrentReference { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public ParameterId ResistanceColumn { get; }
    public Quantity MinimumCurrent { get; }
    public MatrixRouteExpectation? Route { get; }
    public CurrentExcitationConfiguration SourceConfiguration { get; }
    public InstrumentReadFailureKind Kind { get; }
    public int? CommandCode { get; }
    public string Message { get; }
    public Keithley2182Settings MeterSettings { get; }
    public DateTimeOffset SourceConfirmedBefore { get; }
    public DateTimeOffset AttemptStartedAt { get; }
    public DateTimeOffset FailedAt { get; }
    public DateTimeOffset SourceConfirmedAfter { get; }
    public DateTimeOffset IdleConfirmedAt { get; }
    public DateTimeOffset? SourceOffConfirmedAt { get; }
    public CurrentResistanceRetryEvidence? Retry { get; }
    public int Attempts => Retry is null ? 1 : 2;
    public string Status => Kind switch
    {
        InstrumentReadFailureKind.Overrange => "overrange",
        InstrumentReadFailureKind.ReadTimeout or InstrumentReadFailureKind.BufferTimeout => "timeout",
        InstrumentReadFailureKind.CommandRejected when CommandCode == 301 => "overrange",
        InstrumentReadFailureKind.CommandRejected when CommandCode == 419 => "trigger_link_error",
        InstrumentReadFailureKind.CommandRejected when CommandCode == -221 => "settings_conflict",
        _ => "measurement_error",
    };
    public void Validate(ReadCurrentResistanceStep step, MeasurementDataRow row, CurrentSourceModel model)
    {
        if (step.Retry is not { ContinueOnAcquisitionFailure: true } policy || Step != step.Id || Source != step.Source || Meter != step.Meter || CurrentReference != step.CurrentReference ||
            MinimumCurrent != step.MinimumCurrent || VoltageColumn != step.VoltageColumn || CurrentColumn != step.CurrentColumn || ResistanceColumn != step.ResistanceColumn ||
            !Equals(Route, step.RequiredRoute) || SourceConfiguration.Model != model || policy.OwnsSourceOutput != (SourceOffConfirmedAt is not null) || IdleConfirmedAt > row.Timestamp ||
            row.Values.ContainsKey(VoltageColumn) || row.Values.ContainsKey(CurrentColumn) || row.Values.ContainsKey(ResistanceColumn) || row.Quality.Any(q => q.Step == Step) || row.IndependentResistance.Any(e => e.Step == Step))
            throw new ArgumentException("Failed resistance identity/policy/proof differs from plan or claims successful electrical values/quality.");
        Retry?.Validate(policy, SourceConfirmedBefore);
        var expected = step.ExpectedProgrammedCurrent is { } expectedValue ? NumericPlanValue.Evaluate(expectedValue, variable => row.Values.TryGetValue(variable, out var value) ? value : throw new ArgumentException("Failed acquisition requires its persisted excitation axes.")) : (Quantity?)null;
        if (expected is { } requested && (requested.Unit != PhysicalUnit.Ampere || requested.Value != SourceConfiguration.Level)) throw new ArgumentException("Invalid point cannot substitute another excitation.");
    }
}

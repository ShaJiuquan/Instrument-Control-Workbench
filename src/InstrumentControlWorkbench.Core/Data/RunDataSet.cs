using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record RunId
{
    public RunId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Run ID must not be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public static RunId New() => new(Guid.NewGuid());
    public override string ToString() => $"run-{Value:N}";
}

public sealed record MeasurementDataRow
{
    public MeasurementDataRow(DateTimeOffset timestamp, IReadOnlyDictionary<ParameterId, Quantity> values, ResolvedSampleRoute? routing = null, SampleAnalysisSummary? analysis = null, MeasurementGroupSummary? measurementGroup = null, IEnumerable<EnvironmentObservation>? environment = null, IEnumerable<AcquisitionQuality>? quality = null, IEnumerable<IndependentResistanceEvidence>? independentResistance = null, IEnumerable<IndependentResistanceFailure>? acquisitionFailures = null, CalculationSummary? calculation = null, IEnumerable<Sr830AcEvidence>? sr830Ac = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (new[] { routing is not null, analysis is not null, measurementGroup is not null }.Count(v => v) > 1) throw new ArgumentException("A row has one provenance kind: electrode reading, sample analysis or measurement point.");
        Timestamp = timestamp; Calculation = calculation; Routing = routing; Analysis = analysis; MeasurementGroup = measurementGroup;
        var observations = environment?.ToArray() ?? [];
        if (observations.Length > 192 || observations.Any(o => o is null) || observations.Select(o => o.Column).Distinct().Count() != observations.Length)
            throw new ArgumentException("Environment evidence requires bounded distinct column observations.");
        Environment = Array.AsReadOnly(observations);
        var evidence = quality?.ToArray() ?? [];
        if (evidence.Length > 4096 || evidence.Any(q => q is null)) throw new ArgumentException("Acquisition quality evidence exceeds its bound or contains null entries.", nameof(quality));
        Quality = Array.AsReadOnly(evidence);
        var resistance = independentResistance?.ToArray() ?? [];
        if (resistance.Length > 4096 || resistance.Any(r => r is null) || resistance.Select(r => r.Step).Distinct().Count() != resistance.Length)
            throw new ArgumentException("Independent resistance evidence requires bounded, distinct acquisition identities.");
        IndependentResistance = Array.AsReadOnly(resistance);
        var failures = acquisitionFailures?.ToArray() ?? [];
        if (failures.Length > 4096 || failures.Any(f => f is null) || failures.Select(f => f.Step).Distinct().Count() != failures.Length || failures.Any(f => resistance.Any(r => r.Step == f.Step)))
            throw new ArgumentException("Acquisition failures require bounded distinct identities without successful evidence for the same step.");
        AcquisitionFailures = Array.AsReadOnly(failures);
        var ac = sr830Ac?.ToArray() ?? [];
        if (ac.Length > 4096 || ac.Any(e => e is null) || ac.Select(e => e.Step).Distinct().Count() != ac.Length) throw new ArgumentException("AC evidence requires bounded distinct acquisition identities.");
        Sr830Ac = Array.AsReadOnly(ac);
        Values = new ReadOnlyDictionary<ParameterId, Quantity>(new Dictionary<ParameterId, Quantity>(values));
    }

    public IReadOnlyList<Sr830AcEvidence> Sr830Ac { get; }
    public CalculationSummary? Calculation { get; }
    public IReadOnlyList<IndependentResistanceFailure> AcquisitionFailures { get; }
    public IReadOnlyList<IndependentResistanceEvidence> IndependentResistance { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> Values { get; }
    public ResolvedSampleRoute? Routing { get; }
    public SampleAnalysisSummary? Analysis { get; }
    public MeasurementGroupSummary? MeasurementGroup { get; }
    public IReadOnlyList<EnvironmentObservation> Environment { get; }
    public IReadOnlyList<AcquisitionQuality> Quality { get; }
    public ReadingQualityIssues QualityIssues => Quality.Aggregate(ReadingQualityIssues.None, (issues, item) => issues | (item.Status?.Issues ?? ReadingQualityIssues.None));
    public int ObservedQualityColumns => Quality.Count(q => q.Status is not null);
    public ReadingQualityState QualityState => AcquisitionFailures.Count != 0 ? ReadingQualityState.ContainsFailedAcquisitions : ObservedQualityColumns == 0 ? ReadingQualityState.Unobserved :
        ObservedQualityColumns < Quality.Count ? ReadingQualityState.PartiallyObserved :
        QualityIssues == ReadingQualityIssues.None ? ReadingQualityState.ObservedNormal : ReadingQualityState.ObservedWithIssues;
}

public sealed class RunDataSet
{
    private readonly object gate = new();
    private sealed record RetainedRow(MeasurementDataRow Row, long Bytes, string Digest);
    private readonly Queue<RetainedRow> rows = [];
    private readonly IRunRowEncoding? rowEncoding;
    private readonly RunRowRetention? retention;
    private string prefixHash = RunRowWindowProof.EmptyHash;
    private string fullHash = RunRowWindowProof.EmptyHash;
    private int totalRows;
    private long retainedBytes;
    private readonly RunDataRowValidator rowValidator;
    private MeasurementRunState state = MeasurementRunState.Running;
    private DateTimeOffset? finishedAt;
    private RunBlackboardSnapshot? blackboard;

    public RunDataSet(RunId id, string name, DateTimeOffset startedAt, DataSetSchema schema,
        IReadOnlyDictionary<string, Quantity> recipe, IEnumerable<InstrumentParameterSnapshot> instruments,
        ExperimentPlan? plan = null, RunMetadata? metadata = null, IRunRowEncoding? rowEncoding = null, RunRowRetention? retention = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(instruments);
        var snapshots = instruments.ToArray();
        if (snapshots.Any(s => s is null) || snapshots.Select(s => s.InstrumentId).Distinct().Count() != snapshots.Length)
            throw new ArgumentException("Instrument snapshots must be non-null and unique.", nameof(instruments));
        if (rowEncoding is not null) ArgumentException.ThrowIfNullOrWhiteSpace(rowEncoding.RowEncodingId);
        this.rowEncoding = rowEncoding; this.retention = retention;
        Id = id;
        Name = name;
        StartedAt = startedAt;
        Schema = schema;
        Recipe = new ReadOnlyDictionary<string, Quantity>(new Dictionary<string, Quantity>(recipe));
        Instruments = Array.AsReadOnly(snapshots);
        Plan = plan; Metadata = metadata ?? RunMetadata.Empty;
        rowValidator = new(plan, schema);
    }

    public const int FormatVersion = 2;
    public RunId Id { get; }
    public string Name { get; }
    public DateTimeOffset StartedAt { get; }
    public DataSetSchema Schema { get; }
    public IReadOnlyDictionary<string, Quantity> Recipe { get; }
    public IReadOnlyList<InstrumentParameterSnapshot> Instruments { get; }
    public ExperimentPlan? Plan { get; }
    public RunMetadata Metadata { get; }
    public MeasurementRunState State { get { lock (gate) return state; } }
    public DateTimeOffset? FinishedAt { get { lock (gate) return finishedAt; } }
    public int TotalRowCount { get { lock (gate) return totalRows; } }
    public int RowOffset { get { lock (gate) return totalRows - rows.Count; } }
    public long RetainedEncodedBytes { get { lock (gate) return retainedBytes; } }
    public IReadOnlyList<MeasurementDataRow> Rows { get { lock (gate) return Array.AsReadOnly(rows.Select(row => row.Row).ToArray()); } }

    public RunDataSetSnapshot Capture()
    {
        lock (gate)
            return new(Id, Name, StartedAt, finishedAt, state, Schema, Recipe, Instruments, Array.AsReadOnly(rows.Select(row => row.Row).ToArray()), Plan, Metadata, blackboard,
                rowEncoding is null ? null : new RunRowWindowProof(rowEncoding.RowEncodingId, totalRows - rows.Count, totalRows, prefixHash, fullHash));
    }

    public void RequireCapacityForNextRow()
    {
        lock (gate)
            if (retention is not null && rowEncoding is null && totalRows >= retention.MaximumRows) throw new InvalidOperationException("In-memory result capacity reached; configure durable row storage before a longer acquisition.");
    }

    public void Append(MeasurementDataRow row)
    {
        Schema.ValidateRow(row);
        if (row.Routing is { } context)
            (Plan?.Routing ?? throw new ArgumentException("Routed rows require the frozen plan routing configuration.", nameof(row))).ValidateContext(context);
        lock (gate)
        {
            if (state != MeasurementRunState.Running) throw new InvalidOperationException("A finalized data set cannot accept rows.");
            if (retention is not null && rowEncoding is null && totalRows >= retention.MaximumRows) throw new InvalidOperationException("In-memory result capacity reached; configure durable row storage before a longer acquisition.");
            var bytes = rowEncoding is null ? null : rowEncoding.EncodeRow(checked(totalRows + 1), row) ?? throw new InvalidOperationException("Canonical row encoding returned no frame.");
            if (retention is not null && bytes is not null && bytes.Length > retention.MaximumEncodedBytes) throw new InvalidOperationException("A row exceeds the configured live retention byte limit.");
            var nextHash = bytes is null ? fullHash : RunRowWindowProof.AppendHash(fullHash, bytes);
            rowValidator.Append(row); totalRows++; fullHash = nextHash;
            rows.Enqueue(new(row, bytes?.Length ?? 0, nextHash)); retainedBytes += bytes?.Length ?? 0;
            if (retention is not null && rowEncoding is not null)
                while (rows.Count > retention.MaximumRows || retainedBytes > retention.MaximumEncodedBytes)
                { var removed = rows.Dequeue(); retainedBytes -= removed.Bytes; prefixHash = removed.Digest; }
        }
    }

    public void Complete(MeasurementRunState result, DateTimeOffset timestamp, RunBlackboardSnapshot? blackboard = null)
    {
        if (result is not (MeasurementRunState.Completed or MeasurementRunState.Aborted or MeasurementRunState.Faulted))
            throw new ArgumentException("A data set requires a terminal run state.", nameof(result));
        if (timestamp < StartedAt) throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (blackboard is not null) rowValidator.ValidateBlackboard(Id, blackboard);
        lock (gate)
        {
            if (state != MeasurementRunState.Running) throw new InvalidOperationException("The data set is already finalized.");
            state = result;
            finishedAt = timestamp;
            this.blackboard = blackboard;
        }
    }
}

public sealed record RunDataSetSnapshot(
    RunId Id, string Name, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    MeasurementRunState State, DataSetSchema Schema, IReadOnlyDictionary<string, Quantity> Recipe,
    IReadOnlyList<InstrumentParameterSnapshot> Instruments, IReadOnlyList<MeasurementDataRow> Rows,
    ExperimentPlan? Plan = null, RunMetadata? Metadata = null, RunBlackboardSnapshot? Blackboard = null, RunRowWindowProof? RowWindow = null, int CapturedRowOffset = 0, int? AcquiredRowCount = null)
{
    public int RowOffset => RowWindow?.RowOffset ?? CapturedRowOffset;
    public int TotalRowCount => RowWindow?.TotalRows ?? AcquiredRowCount ?? Rows.Count;
    public bool HasCompleteRows => RowOffset == 0 && TotalRowCount == Rows.Count;
}

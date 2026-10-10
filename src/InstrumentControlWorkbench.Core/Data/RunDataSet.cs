using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Experiments;
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
    public MeasurementDataRow(DateTimeOffset timestamp, IReadOnlyDictionary<ParameterId, Quantity> values, ResolvedSampleRoute? routing = null, SampleAnalysisSummary? analysis = null, MeasurementGroupSummary? measurementGroup = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (new[] { routing is not null, analysis is not null, measurementGroup is not null }.Count(v => v) > 1) throw new ArgumentException("A row has one provenance kind: electrode reading, sample analysis or measurement point.");
        Timestamp = timestamp; Routing = routing; Analysis = analysis; MeasurementGroup = measurementGroup;
        Values = new ReadOnlyDictionary<ParameterId, Quantity>(new Dictionary<ParameterId, Quantity>(values));
    }

    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> Values { get; }
    public ResolvedSampleRoute? Routing { get; }
    public SampleAnalysisSummary? Analysis { get; }
    public MeasurementGroupSummary? MeasurementGroup { get; }
}

public sealed class RunDataSet
{
    private readonly object gate = new();
    private readonly List<MeasurementDataRow> rows = [];
    private readonly RunDataRowValidator rowValidator;
    private MeasurementRunState state = MeasurementRunState.Running;
    private DateTimeOffset? finishedAt;

    public RunDataSet(RunId id, string name, DateTimeOffset startedAt, DataSetSchema schema,
        IReadOnlyDictionary<string, Quantity> recipe, IEnumerable<InstrumentParameterSnapshot> instruments,
        ExperimentPlan? plan = null, RunMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(instruments);
        var snapshots = instruments.ToArray();
        if (snapshots.Any(s => s is null) || snapshots.Select(s => s.InstrumentId).Distinct().Count() != snapshots.Length)
            throw new ArgumentException("Instrument snapshots must be non-null and unique.", nameof(instruments));
        Id = id;
        Name = name;
        StartedAt = startedAt;
        Schema = schema;
        Recipe = new ReadOnlyDictionary<string, Quantity>(new Dictionary<string, Quantity>(recipe));
        Instruments = Array.AsReadOnly(snapshots);
        Plan = plan; Metadata = metadata ?? RunMetadata.Empty;
        rowValidator = new(plan, schema);
    }

    public const int FormatVersion = 1;
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
    public IReadOnlyList<MeasurementDataRow> Rows { get { lock (gate) return Array.AsReadOnly(rows.ToArray()); } }

    public RunDataSetSnapshot Capture()
    {
        lock (gate)
            return new(Id, Name, StartedAt, finishedAt, state, Schema, Recipe, Instruments, Array.AsReadOnly(rows.ToArray()), Plan, Metadata);
    }

    public void Append(MeasurementDataRow row)
    {
        Schema.ValidateRow(row);
        if (row.Routing is { } context)
            (Plan?.Routing ?? throw new ArgumentException("Routed rows require the frozen plan routing configuration.", nameof(row))).ValidateContext(context);
        lock (gate)
        {
            if (state != MeasurementRunState.Running) throw new InvalidOperationException("A finalized data set cannot accept rows.");
            rowValidator.Append(row);
            rows.Add(row);
        }
    }

    public void Complete(MeasurementRunState result, DateTimeOffset timestamp)
    {
        if (result is not (MeasurementRunState.Completed or MeasurementRunState.Aborted or MeasurementRunState.Faulted))
            throw new ArgumentException("A data set requires a terminal run state.", nameof(result));
        if (timestamp < StartedAt) throw new ArgumentOutOfRangeException(nameof(timestamp));
        lock (gate)
        {
            if (state != MeasurementRunState.Running) throw new InvalidOperationException("The data set is already finalized.");
            state = result;
            finishedAt = timestamp;
        }
    }
}

public sealed record RunDataSetSnapshot(
    RunId Id, string Name, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    MeasurementRunState State, DataSetSchema Schema, IReadOnlyDictionary<string, Quantity> Recipe,
    IReadOnlyList<InstrumentParameterSnapshot> Instruments, IReadOnlyList<MeasurementDataRow> Rows,
    ExperimentPlan? Plan = null, RunMetadata? Metadata = null);

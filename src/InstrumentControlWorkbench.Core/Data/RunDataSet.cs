using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

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
    public MeasurementDataRow(DateTimeOffset timestamp, IReadOnlyDictionary<ParameterId, Quantity> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Timestamp = timestamp;
        Values = new ReadOnlyDictionary<ParameterId, Quantity>(new Dictionary<ParameterId, Quantity>(values));
    }

    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> Values { get; }
}

public sealed class RunDataSet
{
    private readonly object gate = new();
    private readonly List<MeasurementDataRow> rows = [];
    private MeasurementRunState state = MeasurementRunState.Running;
    private DateTimeOffset? finishedAt;

    public RunDataSet(RunId id, string name, DateTimeOffset startedAt, DataSetSchema schema,
        IReadOnlyDictionary<string, Quantity> recipe, IEnumerable<InstrumentParameterSnapshot> instruments)
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
    }

    public const int FormatVersion = 1;
    public RunId Id { get; }
    public string Name { get; }
    public DateTimeOffset StartedAt { get; }
    public DataSetSchema Schema { get; }
    public IReadOnlyDictionary<string, Quantity> Recipe { get; }
    public IReadOnlyList<InstrumentParameterSnapshot> Instruments { get; }
    public MeasurementRunState State { get { lock (gate) return state; } }
    public DateTimeOffset? FinishedAt { get { lock (gate) return finishedAt; } }
    public IReadOnlyList<MeasurementDataRow> Rows { get { lock (gate) return Array.AsReadOnly(rows.ToArray()); } }

    public void Append(MeasurementDataRow row)
    {
        Schema.ValidateRow(row);
        lock (gate)
        {
            if (state != MeasurementRunState.Running) throw new InvalidOperationException("A finalized data set cannot accept rows.");
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

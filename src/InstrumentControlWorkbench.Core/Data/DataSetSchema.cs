using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record DataColumn
{
    public DataColumn(ParameterDescriptor parameter, IEnumerable<ParameterId>? dependsOn = null, bool optional = false, ParameterAddress? source = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var dependencies = dependsOn?.ToArray() ?? [];
        if (dependencies.Any(id => id is null) || dependencies.Distinct().Count() != dependencies.Length)
            throw new ArgumentException("Column dependencies must be non-null and unique.", nameof(dependsOn));
        Parameter = parameter;
        DependsOn = Array.AsReadOnly(dependencies);
        Optional = optional;
        Source = source;
    }

    public ParameterDescriptor Parameter { get; }
    public IReadOnlyList<ParameterId> DependsOn { get; }
    public bool Optional { get; }
    public ParameterAddress? Source { get; }
}

public sealed class DataSetSchema
{
    private readonly IReadOnlyDictionary<ParameterId, DataColumn> byId;

    public DataSetSchema(IEnumerable<DataColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var items = columns.ToArray();
        if (items.Length == 0 || items.Any(c => c is null)) throw new ArgumentException("A schema needs non-null columns.", nameof(columns));
        if (items.Select(c => c.Parameter.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("Duplicate data column ID.", nameof(columns));
        byId = items.ToDictionary(c => c.Parameter.Id);
        var visited = new HashSet<ParameterId>();
        var visiting = new HashSet<ParameterId>();
        foreach (var column in items) Visit(column.Parameter.Id);
        Columns = Array.AsReadOnly(items);

        void Visit(ParameterId id)
        {
            if (visited.Contains(id)) return;
            if (!byId.TryGetValue(id, out var column)) throw new ArgumentException($"Unknown dependency {id}.", nameof(columns));
            if (!visiting.Add(id)) throw new ArgumentException($"Cyclic column dependency at {id}.", nameof(columns));
            foreach (var dependency in column.DependsOn) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
    }

    public IReadOnlyList<DataColumn> Columns { get; }

    public void ValidateRow(MeasurementDataRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Values.Keys.Any(id => !byId.ContainsKey(id))) throw new ArgumentException("A row contains an unknown schema column.", nameof(row));
        foreach (var (id, column) in byId)
        {
            if (!row.Values.TryGetValue(id, out var value))
            {
                if (column.Optional) continue;
                throw new ArgumentException($"Missing data column {id}.", nameof(row));
            }
            foreach (var dependency in column.DependsOn)
                if (!row.Values.ContainsKey(dependency)) throw new ArgumentException($"{id} requires dependency {dependency} in this row.", nameof(row));
            column.Parameter.Validate(value);
        }
    }
}

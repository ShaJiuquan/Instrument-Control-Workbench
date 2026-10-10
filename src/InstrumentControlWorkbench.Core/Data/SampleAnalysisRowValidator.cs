using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public sealed record SampleAnalysisSummary
{
    public SampleAnalysisSummary(StepId step, int firstInputRow, int inputCount, SampleAnalysisIssues issues, int algorithmVersion = SampleResistanceAnalysis.AlgorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (algorithmVersion != SampleResistanceAnalysis.AlgorithmVersion) throw new NotSupportedException("Unsupported sample analysis algorithm version.");
        if (firstInputRow < 1 || inputCount is not (2 or 8) || (issues & ~(SampleAnalysisIssues.MissingGeometry | SampleAnalysisIssues.NonPositiveVanDerPauwMean | SampleAnalysisIssues.UnrepresentableResult)) != 0)
            throw new ArgumentOutOfRangeException(nameof(firstInputRow));
        Step = step; FirstInputRow = firstInputRow; InputCount = inputCount; Issues = issues; AlgorithmVersion = algorithmVersion;
    }
    public StepId Step { get; }
    public int FirstInputRow { get; }
    public int InputCount { get; }
    public SampleAnalysisIssues Issues { get; }
    public int AlgorithmVersion { get; }
}

// The same bounded, ordered evidence check is used for live append, archive
// writing and streaming archive reading. Only eight preceding rows are retained.
public sealed class SampleAnalysisRowValidator
{
    private readonly ExperimentPlan? plan;
    private readonly DataSetSchema schema;
    private readonly Dictionary<StepId, SampleAnalysisStep> groups = [];
    private readonly Queue<MeasurementDataRow> recent = new();
    private int count;
    public bool HasGroups => groups.Count != 0;
    public SampleAnalysisRowValidator(ExperimentPlan? plan, DataSetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema); this.plan = plan; this.schema = schema;
        if (plan is null) return;
        var pending = new Stack<(ExperimentStep Step, int Depth)>(); pending.Push((plan.Root, 0)); var visited = 0;
        while (pending.TryPop(out var item))
        {
            if (++visited > 10_000 || item.Depth > 128) throw new ArgumentException("Analysis plan exceeds the document node/depth budget.", nameof(plan));
            switch (item.Step)
            {
                case SampleAnalysisStep group:
                    group.ValidateRouting(plan.Routing ?? throw new ArgumentException("Sample analysis requires routing.", nameof(plan)));
                    foreach (var result in group.Columns)
                    {
                        var column = schema.Columns.SingleOrDefault(c => c.Parameter.Id == result.Column);
                        if (column is null || column.Parameter.Unit != SampleResistanceAnalysis.Unit(result.Kind) || column.Source is not null)
                            throw new ArgumentException("Analysis schema needs the declared derived columns and units.", nameof(schema));
                    }
                    if (!groups.TryAdd(group.Id, group)) throw new ArgumentException("Duplicate sample analysis step ID.", nameof(plan));
                    foreach (var read in group.Readings) pending.Push((read, item.Depth + 1));
                    break;
                case MeasurementGroupStep point: foreach (var member in point.Members) pending.Push((member.Body, item.Depth + 1)); break;
                case SequenceStep sequence: foreach (var child in sequence.Children) pending.Push((child, item.Depth + 1)); break;
                case ScanStep scan: pending.Push((scan.Body, item.Depth + 1)); break;
                case ForEachStep loop: pending.Push((loop.Body, item.Depth + 1)); break;
                case RepeatStep repeat: pending.Push((repeat.Body, item.Depth + 1)); break;
                case IfElseStep choice:
                    pending.Push((choice.Then, item.Depth + 1)); if (choice.Else is { } other) pending.Push((other, item.Depth + 1)); break;
            }
        }
    }
    public void Validate(MeasurementDataRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Analysis is not { } summary)
        {
            if (groups.Values.Any(g => g.Columns.Any(c => row.Values.ContainsKey(c.Column))))
                throw new ArgumentException("Declared derived quantities require their analysis provenance.", nameof(row));
            return;
        }
        if (row.Routing is not null || !groups.TryGetValue(summary.Step, out var group) ||
            summary.InputCount != group.Readings.Count || summary.FirstInputRow != count + 1 - summary.InputCount || recent.Count < summary.InputCount)
            throw new ArgumentException("Summary must reference its declared group's complete immediately preceding raw rows.", nameof(row));
        var input = recent.TakeLast(summary.InputCount).ToArray();
        var derived = group.Columns.Select(c => schema.Columns.Single(s => s.Parameter.Id == c.Column)).ToArray();
        var elapsed = new ParameterId("elapsed");
        var axes = derived[0].DependsOn.Where(id => id != elapsed).ToArray();
        if (derived.Any(c => !c.DependsOn.SequenceEqual(derived[0].DependsOn)) || !derived[0].DependsOn.Contains(elapsed))
            throw new ArgumentException("Summary schema must preserve identical scan dependencies and elapsed time.");
        var resistances = new List<Quantity>();
        for (var i = 0; i < input.Length; i++)
        {
            var read = group.Readings[i]; var raw = input[i];
            var expected = plan!.Routing!.Resolve(read.Binding);
            if (raw.Analysis is not null || raw.Routing is not { } actual || actual.Binding != expected.Binding)
                throw new ArgumentException("Summary input selection, sample or wiring differs from the declared group.");
            plan.Routing.ValidateContext(actual);
            foreach (var axis in axes)
                if (!row.Values.TryGetValue(axis, out var target) || !raw.Values.TryGetValue(axis, out var value) || target != value)
                    throw new ArgumentException("Summary inputs span different scan points.");
            if (!raw.Values.TryGetValue(read.Acquisition.VoltageColumn, out var voltage) || voltage.Unit != PhysicalUnit.Volt ||
                !raw.Values.TryGetValue(read.Acquisition.CurrentColumn, out var current) || current.Unit != PhysicalUnit.Ampere || Math.Abs(current.Value) <= read.Acquisition.MinimumCurrent.Value ||
                !raw.Values.TryGetValue(read.Acquisition.ResistanceColumn, out var resistance) || resistance.Unit != PhysicalUnit.Ohm || resistance.Value != voltage.Value / current.Value)
                throw new ArgumentException("Summary inputs need valid coherent V/I/R values.");
            resistances.Add(resistance);
        }
        var sample = group.ValidateRouting(plan!.Routing!);
        var result = SampleResistanceAnalysis.Calculate(sample.Topology, sample.Geometry, resistances);
        if (result.Issues != summary.Issues) throw new ArgumentException("Summary issue flags differ from measured-input analysis.");
        var expectedValues = new Dictionary<ParameterId, Quantity>();
        foreach (var column in group.Columns)
            if (result.Values.TryGetValue(column.Kind, out var value)) expectedValues.Add(column.Column, value);
        var allowed = expectedValues.Keys.Concat(axes).Append(elapsed).ToHashSet();
        // Math.Log/Exp may differ by a few ulps across supported OS runtimes.
        // Finalized checksums still protect the exact bytes that were recorded.
        if (!row.Values.Keys.ToHashSet().SetEquals(allowed) || expectedValues.Any(p => !row.Values.TryGetValue(p.Key, out var actual) || actual.Unit != p.Value.Unit ||
            actual.Value != p.Value.Value && (p.Value.Value == 0 || Math.Abs(actual.Value / p.Value.Value - 1) > 1e-12)))
            throw new ArgumentException("Summary values differ from recomputed raw inputs.");
    }
    public void Append(MeasurementDataRow row)
    {
        Validate(row); AppendValidated(row);
    }
    internal void AppendValidated(MeasurementDataRow row)
    {
        count++;
        if (groups.Count == 0) return;
        recent.Enqueue(row); if (recent.Count > 8) recent.Dequeue();
    }
}

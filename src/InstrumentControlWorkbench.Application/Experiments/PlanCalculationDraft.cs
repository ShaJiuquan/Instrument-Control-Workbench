using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed class PlanCalculationDraft
{
    public PlanCalculationDraft(StepId id, ExperimentStep body, IEnumerable<CalculatedColumn?> columns)
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(columns);
        var items = columns.ToArray(); if (items.Length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(columns));
        Id = id; Body = body; Columns = Array.AsReadOnly(items);
    }
    public StepId Id { get; }
    public ExperimentStep Body { get; }
    public IReadOnlyList<CalculatedColumn?> Columns { get; }
}
public sealed record PlanCalculationColumnPreview(IReadOnlyList<PlanVariableAvailability> Inputs, PhysicalUnit? Unit, string? Error);
public sealed record PlanCalculationDraftPreview(IReadOnlyList<PlanVariableAvailability> RowInputs, IReadOnlyList<PlanCalculationColumnPreview> Columns);

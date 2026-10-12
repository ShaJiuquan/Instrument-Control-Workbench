using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

internal static partial class ExperimentPlanCompiler
{
    private sealed record CalculationProbe(StepId Target, PlanStructureEditKind Kind, PlanCalculationDraft Draft);
    // A metadata-only inspection must never return a partly compiled runnable plan.
    private sealed class CalculationInspectionComplete(CalculationProbe probe, PlanCalculationDraftPreview preview) : Exception
    { public CalculationProbe Probe { get; } = probe; public PlanCalculationDraftPreview Preview { get; } = preview; }

    public static PlanCalculationDraftPreview InspectCalculationDraft(ExperimentPlan plan, DeviceRegistry registry, StepId target, PlanStructureEditKind kind, PlanCalculationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(draft);
        if (kind is not (PlanStructureEditKind.Replace or PlanStructureEditKind.Append)) throw new ArgumentOutOfRangeException(nameof(kind));
        Compile(plan, registry); // The host plan must still be valid; draft inspection cannot admit a run.
        var probe = new CalculationProbe(target, kind, draft);
        try { CompileCore(plan, registry, null, PlanVariablePosition.BeforeStep, probe); }
        catch (CalculationInspectionComplete completed) when (ReferenceEquals(completed.Probe, probe)) { return completed.Preview; }
        throw new ArgumentException("The selected position cannot inspect this calculation draft.", nameof(target));
    }

    private static PlanCalculationDraftPreview DescribeRowCalculations(CalculationProbe probe, IReadOnlyList<ParameterId> axes, VariableFlowState known, IReadOnlyDictionary<ParameterId, DataColumn> schema)
    {
        var draft = probe.Draft;
        var members = draft.Body is MeasurementGroupStep group ? group.Members : new[] { new MeasurementGroupMember("calculation input", draft.Body) };
        var outputIds = members.SelectMany(member => member.OutputColumns).ToArray();
        var environmentIds = members.SelectMany(member => member.EnvironmentReads).Select(read => read.Column).ToHashSet();
        var rowInputs = outputIds.Concat(axes).Append(Elapsed.Id).Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).Select(id =>
        {
            var column = schema[id]; var definitions = known.Definitions(id);
            return new PlanVariableAvailability(id, BlackboardValueType.Quantity(column.Parameter.Unit), axes.Contains(id), definitions.Count == 0 ? Array.AsReadOnly(new[] { draft.Body.Id }) : definitions, id != Elapsed.Id && !axes.Contains(id) && (environmentIds.Contains(id) || !known.ContainsKey(id)));
        }).ToArray();
        var available = rowInputs.ToDictionary(input => input.Id); var previews = new List<PlanCalculationColumnPreview>();
        foreach (var result in draft.Columns)
        {
            var inputs = Array.AsReadOnly(available.Values.OrderBy(input => input.Id.Value, StringComparer.Ordinal).ToArray());
            PhysicalUnit? unit = null; string? error = null;
            try
            {
                if (result is null) throw new ArgumentException("Complete the result column ID and numeric expression.");
                if (schema.ContainsKey(result.Column) || available.ContainsKey(result.Column)) throw new ArgumentException("Result columns must be distinct from existing columns and prior results.");
                unit = NumericPlanValue.InferUnit(result.Expression, id => available.TryGetValue(id, out var input) ? input.Type.Unit!.Value : throw new ArgumentException($"{id} is not a current-row input or earlier valid result."));
                available.Add(result.Column, new(result.Column, BlackboardValueType.Quantity(unit.Value), false, Array.AsReadOnly(new[] { draft.Id }), true));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or ArithmeticException)
            { unit = null; error = ex.Message; }
            previews.Add(new(inputs, unit, error));
        }
        return new(Array.AsReadOnly(rowInputs), Array.AsReadOnly(previews.ToArray()));
    }
}

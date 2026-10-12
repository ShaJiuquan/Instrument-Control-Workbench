using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    public IReadOnlyList<ExperimentStep> ReadSteps(ExperimentPlan plan, IReadOnlyList<StepId> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count > 10_000 || selected.Any(id => id is null) || selected.Distinct().Count() != selected.Count) throw new ArgumentException("Select bounded distinct step IDs.", nameof(selected));
        var root = StructureRoot(plan); var entries = WalkStructure((JsonObject)root["root"]!).ToDictionary(entry => new StepId(Text(entry.Node, "id")));
        var count = 0;
        return System.Array.AsReadOnly(selected.Select(id => DecodeStep((entries.TryGetValue(id, out var entry) ? entry : throw new ArgumentException($"Unknown step {id}.")).Node, 0, ref count)).ToArray());
    }
    public ExperimentPlan ReplaceSteps(ExperimentPlan plan, IReadOnlyList<ExperimentStep> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count is < 1 or > 10_000 || replacements.Any(step => step is null) || replacements.Select(step => step.Id).Distinct().Count() != replacements.Count) throw new ArgumentException("Replace bounded distinct steps.", nameof(replacements));
        var root = StructureRoot(plan); var entries = WalkStructure((JsonObject)root["root"]!); var byId = entries.ToDictionary(entry => new StepId(Text(entry.Node, "id"))); var byNode = entries.ToDictionary(entry => entry.Node);
        var ids = replacements.Select(step => step.Id).ToHashSet();
        foreach (var step in replacements)
        {
            if (!byId.TryGetValue(step.Id, out var entry)) throw new ArgumentException($"Unknown step {step.Id}.");
            for (var parent = entry.Parent; parent is not null; parent = byNode[parent].Parent)
                if (ids.Contains(new StepId(Text(parent, "id")))) throw new ArgumentException("A batch cannot replace overlapping parent/child steps.");
        }
        var count = 0;
        foreach (var step in replacements)
        {
            var entry = byId[step.Id]; var replacement = EncodeStep(step, 0, ref count);
            if (entry.Parent is null) root["root"] = replacement;
            else if (entry.Container is JsonArray array) array[entry.Position] = replacement;
            else ((JsonObject)entry.Container!)[entry.Slot] = replacement;
        }
        // No intermediate candidate escapes: parse the complete final envelope.
        return Parse(root.ToJsonString(JsonOptions));
    }
}

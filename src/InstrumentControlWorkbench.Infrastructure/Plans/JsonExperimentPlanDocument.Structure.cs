using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private sealed record StructureEntry(JsonObject Node, JsonObject? Parent, JsonNode? Container, string Slot, int Position, int Depth);
    public IReadOnlyList<PlanStructureNode> DescribeStructure(ExperimentPlan plan)
    {
        var root = StructureRoot(plan); var entries = WalkStructure((JsonObject)root["root"]!);
        return entries.Select(e =>
        {
            var sequenceChild = e.Parent?["kind"]?.GetValue<string>() == "sequence" && e.Slot == "children";
            return new PlanStructureNode(new(Text(e.Node, "id")), Text(e.Node, "kind"), e.Parent is null ? null : new(Text(e.Parent, "id")), e.Slot, e.Position, e.Depth,
                Text(e.Node, "kind") == "sequence", sequenceChild, sequenceChild && e.Position > 0,
                sequenceChild && e.Container is JsonArray array && e.Position < array.Count - 1);
        }).ToArray();
    }
    public string ReadFragment(ExperimentPlan plan, StepId selected)
        => FindStructure(StructureRoot(plan), selected).Node.ToJsonString(JsonOptions);
    public ExperimentStep ReadStep(ExperimentPlan plan, StepId selected)
    {
        var count = 0;
        return DecodeStep(FindStructure(StructureRoot(plan), selected).Node, 0, ref count);
    }
    public ExperimentPlan EditStep(ExperimentPlan plan, PlanStructureEditKind kind, StepId selected, ExperimentStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (kind is not (PlanStructureEditKind.Replace or PlanStructureEditKind.Append)) throw new ArgumentOutOfRangeException(nameof(kind));
        var count = 0;
        return EditStructure(plan, new(kind, selected, EncodeStep(step, 0, ref count).ToJsonString(JsonOptions)));
    }
    public ExperimentPlan EditStructure(ExperimentPlan plan, PlanStructureEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit); ArgumentNullException.ThrowIfNull(edit.Selected);
        if (!Enum.IsDefined(edit.Kind)) throw new ArgumentOutOfRangeException(nameof(edit));
        var root = StructureRoot(plan); var entry = FindStructure(root, edit.Selected);
        JsonObject? replacement = null;
        if (edit.Kind is PlanStructureEditKind.Replace or PlanStructureEditKind.Append)
        {
            ArgumentNullException.ThrowIfNull(edit.Fragment);
            if (Encoding.UTF8.GetByteCount(edit.Fragment) > MaximumBytes) throw new FormatException("Step fragment exceeds the document byte budget.");
            replacement = JsonNode.Parse(edit.Fragment, documentOptions: new JsonDocumentOptions { MaxDepth = 512 }) as JsonObject ?? throw new FormatException("A step fragment must be an object.");
        }
        else if (edit.Fragment is not null) throw new ArgumentException("This structure operation has no fragment.", nameof(edit));
        if (edit.Kind == PlanStructureEditKind.Replace)
        {
            if (Text(replacement!, "id") != edit.Selected.Value) throw new FormatException("Replacement must retain the selected step ID.");
            if (entry.Parent is null) root["root"] = replacement;
            else if (entry.Container is JsonArray array) array[entry.Position] = replacement;
            else ((JsonObject)entry.Container!)[entry.Slot] = replacement;
        }
        else if (edit.Kind == PlanStructureEditKind.Append)
        {
            if (Text(entry.Node, "kind") != "sequence") throw new InvalidOperationException("Only an ordinary sequence accepts palette children.");
            ((JsonArray)entry.Node["children"]!).Add(replacement);
        }
        else
        {
            if (entry.Parent?["kind"]?.GetValue<string>() != "sequence" || entry.Slot != "children" || entry.Container is not JsonArray children)
                throw new InvalidOperationException("Only a direct ordinary-sequence child can be removed or reordered.");
            if (edit.Kind == PlanStructureEditKind.Remove) children.RemoveAt(entry.Position);
            else
            {
                var destination = entry.Position + (edit.Kind == PlanStructureEditKind.MoveEarlier ? -1 : 1);
                if (destination < 0 || destination >= children.Count) throw new InvalidOperationException("The step is already at the sequence boundary.");
                children.RemoveAt(entry.Position); children.Insert(destination, entry.Node);
            }
        }
        // Decode the entire envelope again: IDs/units/required slots/nested shape
        // and all original routing/preparations/metadata remain codec-validated.
        return Parse(root.ToJsonString(JsonOptions));
    }
    private JsonObject StructureRoot(ExperimentPlan plan) => (JsonObject)JsonNode.Parse(Serialize(plan), documentOptions: new JsonDocumentOptions { MaxDepth = 512 })!;
    private static StructureEntry FindStructure(JsonObject root, StepId selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return WalkStructure((JsonObject)root["root"]!).SingleOrDefault(e => Text(e.Node, "id") == selected.Value)
            ?? throw new ArgumentException("The selected step does not exist in this plan.", nameof(selected));
    }
    private static IReadOnlyList<StructureEntry> WalkStructure(JsonObject root)
    {
        var result = new List<StructureEntry>(); var pending = new Stack<StructureEntry>(); pending.Push(new(root, null, null, "root", 0, 0));
        while (pending.TryPop(out var entry))
        {
            var count = result.Count; CheckSize(entry.Depth, ref count); result.Add(entry);
            var descendants = new List<StructureEntry>();
            // These are actual native codec step slots. Value expressions,
            // conditions and parameter addresses are never mistaken for steps.
            foreach (var slot in new[] { "condition", "body", "then", "else", "success", "failure" })
                if (entry.Node[slot] is JsonObject child && child.ContainsKey("id") && child.ContainsKey("kind")) descendants.Add(new(child, entry.Node, entry.Node, slot, 0, entry.Depth + 1));
            foreach (var slot in new[] { "children", "readings" })
                if (entry.Node[slot] is JsonArray children)
                    for (var i = 0; i < children.Count; i++) descendants.Add(new((JsonObject)children[i]!, entry.Node, children, slot, i, entry.Depth + 1));
            if (entry.Node["members"] is JsonArray members)
                for (var i = 0; i < members.Count; i++)
                {
                    var member = (JsonObject)members[i]!; descendants.Add(new((JsonObject)member["body"]!, entry.Node, member, "body", i, entry.Depth + 1));
                }
            for (var i = descendants.Count - 1; i >= 0; i--) pending.Push(descendants[i]);
        }
        return result;
    }
}

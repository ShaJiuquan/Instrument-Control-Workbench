using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep IterationScan(string name, JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        Fields(p, "axis", "points", "points_mode", "start", "end", "step", "values", "segments");
        if (Canonical(Text(p, "axis", name)) != name) throw new FormatException("Iteration axis conflicts with its scan name.");
        JsonNode spec;
        if (p["points"] is JsonObject or JsonArray)
        {
            if (p.Any(field => field.Key is not ("axis" or "points"))) throw new FormatException("Nested points cannot be combined with flat point controls.");
            spec = p["points"]!;
        }
        else
        {
            var flat = (JsonObject)p.DeepClone(); flat.Remove("axis");
            var mode = Text(flat, "points_mode", flat.ContainsKey("segments") ? "segments" : flat.ContainsKey("step") ? "step" : "linear").Trim().ToLowerInvariant();
            flat.Remove("points_mode");
            if (mode == "segments")
            {
                Fields(flat, "segments");
                spec = flat;
            }
            else
            {
                if (flat.ContainsKey("segments")) throw new FormatException("Segments require segments point mode.");
                flat["mode"] = mode; spec = flat;
            }
        }
        var time = name == "scan_time";
        var values = Points(spec, time ? PhysicalUnit.Second : PhysicalUnit.Dimensionless, c, 0);
        if (!time)
        {
            if (values.Values.Any(q => q.Value < 0 || Math.Abs(q.Value - Math.Round(q.Value)) > 1e-9))
                throw new FormatException("scan_number points must be nonnegative integers.");
            values = new(values.Values.Select(q => new Quantity(Math.Round(q.Value), PhysicalUnit.Dimensionless)));
        }
        return new ForEachStep(new(path + ".scan"), new(path + ".axis"), values,
            new SequenceStep(new(path + ".body"), [.. ScanAxisVariables(name, new(path + ".axis"), path), .. ScanChildren(node, path, c, depth)]),
            time ? IterationTiming.DelayBeforePoint : IterationTiming.Immediate);
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static List<ExperimentStep> ScanChildren(JsonObject node, string path, ImportContext c, int depth)
    {
        var result = new List<ExperimentStep>(); var pending = new List<MeasurementGroupMember>(); var ordinal = 0;
        void Flush()
        {
            if (pending.Count == 1) result.Add(pending[0].Body);
            else if (pending.Count > 1) result.Add(new MeasurementGroupStep(new(path + ".group." + ordinal++), pending));
            pending.Clear();
        }
        void Visit(JsonObject parent, string parentPath, int parentDepth)
        {
            var children = parent["children"] as JsonArray ?? throw new FormatException("Scan/parallel children must be an array.");
            for (var i = 0; i < children.Count; i++)
            {
                var child = Object(children[i]); var childPath = parentPath + "." + i;
                var type = Canonical(Text(child, "type", "measurement"));
                if (type == "parallel")
                {
                    if (parentDepth + 1 > 128 || ++c.Nodes > 10_000) throw new FormatException("Parallel scan children exceed node/depth limits.");
                    Fields(child, "type", "name", "label", "id", "source_line", "params", "children");
                    if (child.ContainsKey("params")) Fields(Object(child["params"]));
                    if (child.ContainsKey("source_line"))
                    {
                        var line = Number(child, "source_line");
                        if (line < 1 || line != Math.Truncate(line)) throw new FormatException("Parallel source_line must be a positive integer.");
                        c.Metadata[childPath + ".source_line"] = new(line, PhysicalUnit.Dimensionless);
                    }
                    Visit(child, childPath, parentDepth + 1); continue;
                }
                if (type == "measurement")
                {
                    var body = Decode(child, childPath, c, parentDepth + 1);
                    var p = child["params"] as JsonObject ?? new JsonObject();
                    var label = Text(child, "label", "").Trim();
                    if (label.Length == 0 && (p["sample_id"] ?? p["sample"]) is { } sample)
                        label = c.Bindings.Samples!.ResolveSample(sample.GetValue<string>()).Name;
                    if (label.Length == 0 && p["channel"] is { } channel)
                        label = c.Bindings.ChannelProfiles is { } profiles && !channel.GetValue<string>().Equals("none", StringComparison.OrdinalIgnoreCase)
                            ? profiles.Catalog.ResolveChannel(channel.GetValue<string>()).Name : channel.GetValue<string>();
                    if (label.Length == 0) label = Text(child, "name", Text(p, "name", "Measurement"));
                    pending.Add(new(label, body));
                    if (pending.Count > 64) throw new FormatException("Scan measurement group exceeds 64 members.");
                    continue;
                }
                Flush(); result.Add(Decode(child, childPath, c, parentDepth + 1));
            }
        }
        Visit(node, path, depth); Flush(); return result;
    }
}

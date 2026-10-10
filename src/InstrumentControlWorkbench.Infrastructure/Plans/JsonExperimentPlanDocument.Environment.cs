using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static void EncodeEnvironment(JsonObject node, IReadOnlyList<EnvironmentRead> reads)
    {
        if (reads.Count == 0) return;
        node["environmentReads"] = new JsonArray(reads.Select(read => (JsonNode)new JsonObject
        { ["role"] = read.Role.ToString(), ["column"] = read.Column.Value, ["address"] = read.Address is null ? null : EncodeAddress(read.Address) }).ToArray());
    }
    private static IEnumerable<EnvironmentRead> DecodeEnvironment(JsonObject node)
        => !node.ContainsKey("environmentReads") ? [] : Array(Required(node, "environmentReads")).Select(value =>
        {
            var read = Object(value, "role", "column", "address");
            return new EnvironmentRead(NamedEnum<EnvironmentRole>(read, "role"), new(Text(read, "column")), read["address"] is null ? null : DecodeAddress(read["address"]));
        });
}

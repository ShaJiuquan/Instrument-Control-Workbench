using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeRouting(RoutingConfiguration config) => new()
    {
        ["matrix"] = config.Matrix.Value,
        ["channels"] = new JsonArray(config.Channels.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id.Value,
            ["name"] = c.Name,
            ["connections"] = new JsonArray(c.Connections.Select(v => (JsonNode)new JsonObject
            { ["key"] = v.Key.Value, ["slot"] = v.Slot, ["column"] = v.Column, ["enabled"] = v.Enabled }).ToArray()),
        }).ToArray()),
        ["wirings"] = new JsonArray(config.Wirings.Select(w => (JsonNode)new JsonObject
        {
            ["id"] = w.Id.Value,
            ["sourceMeter"] = w.SourceMeter.Value,
            ["sourcePlus"] = EncodePort(w.SourcePlus),
            ["sourceMinus"] = EncodePort(w.SourceMinus),
            ["sensePlus"] = EncodePort(w.SensePlus),
            ["senseMinus"] = EncodePort(w.SenseMinus),
        }).ToArray()),
        ["samples"] = new JsonArray(config.Samples.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id.Value,
            ["name"] = s.Name,
            ["topology"] = s.Topology.ToString(),
            ["electrodes"] = new JsonArray(s.Electrodes.Select(e => (JsonNode)new JsonObject
            { ["label"] = e.Label.Value, ["channel"] = e.Channel.Value, ["connection"] = e.Connection.Value, ["enabled"] = e.Enabled }).ToArray()),
        }).ToArray()),
    };
    private static RoutingConfiguration DecodeRouting(JsonNode node)
    {
        var o = Object(node, "matrix", "channels", "wirings", "samples");
        var channels = Array(Required(o, "channels")).Select(v =>
        {
            var c = Object(v, "id", "name", "connections");
            return new ChannelProfile(new(Text(c, "id")), Text(c, "name"), Array(Required(c, "connections")).Select(i =>
            {
                var connection = Object(i, "key", "slot", "column", "enabled");
                return new ChannelConnection(new(Text(connection, "key")), connection["slot"]?.GetValue<int>(), connection["column"]?.GetValue<int>(), Boolean(connection, "enabled"));
            }));
        });
        var wirings = Array(Required(o, "wirings")).Select(v =>
        {
            var w = Object(v, "id", "sourceMeter", "sourcePlus", "sourceMinus", "sensePlus", "senseMinus");
            return new InstrumentWiringProfile(new(Text(w, "id")), new(Text(w, "sourceMeter")), DecodePort(w["sourcePlus"]), DecodePort(w["sourceMinus"]), DecodePort(w["sensePlus"]), DecodePort(w["senseMinus"]));
        });
        var samples = Array(Required(o, "samples")).Select(v =>
        {
            var s = Object(v, "id", "name", "topology", "electrodes");
            return new SampleProfile(new(Text(s, "id")), Text(s, "name"), NamedEnum<SampleTopology>(s, "topology"), Array(Required(s, "electrodes")).Select(i =>
            {
                var e = Object(i, "label", "channel", "connection", "enabled");
                return new SampleElectrode(new(Text(e, "label")), new(Text(e, "channel")), new(Text(e, "connection")), Boolean(e, "enabled"));
            }));
        });
        return new(new(Text(o, "matrix")), channels, wirings, samples);
    }
    private static JsonObject EncodePort(MatrixPort port) => new() { ["slot"] = port.Slot, ["row"] = port.Row };
    private static MatrixPort DecodePort(JsonNode? node)
    {
        var o = Object(node, "slot", "row"); return new(Integer(o, "slot"), Integer(o, "row"));
    }
    private static JsonObject EncodeBinding(SampleRoutingBinding binding) => new()
    { ["sample"] = binding.Sample.Value, ["wiring"] = binding.Wiring.Value, ["selection"] = binding.Selection.ToString() };
    private static SampleRoutingBinding DecodeBinding(JsonNode? node)
    {
        var o = Object(node, "sample", "wiring", "selection");
        return new(new(Text(o, "sample")), new(Text(o, "wiring")), NamedEnum<SampleRouteSelection>(o, "selection"));
    }
}

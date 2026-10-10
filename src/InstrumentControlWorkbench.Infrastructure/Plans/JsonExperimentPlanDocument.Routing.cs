using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeRouting(RoutingConfiguration config) => new()
    {
        ["matrix"] = config.Matrix.Value,
        ["channels"] = new JsonArray(config.Channels.Select(c => (JsonNode)EncodeChannel(c)).ToArray()),
        ["wirings"] = new JsonArray(config.Wirings.Select(w =>
        {
            var result = new JsonObject
            {
                ["id"] = w.Id.Value,
                ["sourceMeter"] = w.SourceMeter.Value,
                ["sourcePlus"] = EncodePort(w.SourcePlus),
                ["sourceMinus"] = EncodePort(w.SourceMinus),
                ["sensePlus"] = EncodePort(w.SensePlus),
                ["senseMinus"] = EncodePort(w.SenseMinus),
            };
            OptionalText(result, "label", w.Label); return (JsonNode)result;
        }).ToArray()),
        ["samples"] = new JsonArray(config.Samples.Select(s => (JsonNode)EncodeSample(s)).ToArray()),
    };
    private static JsonObject EncodeChannel(ChannelProfile channel)
    {
        var result = new JsonObject
        {
            ["id"] = channel.Id.Value,
            ["name"] = channel.Name,
            ["connections"] = new JsonArray(channel.Connections.Select(c => (JsonNode)EncodeConnection(c)).ToArray()),
        };
        OptionalText(result, "originalId", channel.OriginalId); OptionalText(result, "alias", channel.Alias); OptionalText(result, "notes", channel.Notes);
        if (channel.GroupIndex is { } group) result["groupIndex"] = group;
        return result;
    }
    private static JsonObject EncodeConnection(ChannelConnection connection)
    {
        var result = new JsonObject { ["key"] = connection.Key.Value, ["slot"] = connection.Slot, ["column"] = connection.Column, ["enabled"] = connection.Enabled };
        OptionalText(result, "label", connection.Label); OptionalText(result, "raw", connection.Raw); OptionalText(result, "notes", connection.Notes);
        if (connection.Index is { } index) result["index"] = index;
        if (connection.DisplayRow is { } row) result["displayRow"] = row;
        return result;
    }
    private static void OptionalText(JsonObject target, string key, string value)
    { if (value.Length != 0) target[key] = value; }
    private static string ProfileText(JsonObject source, string key) => source.ContainsKey(key) ? Text(source, key) : "";
    private static JsonObject EncodeSample(SampleProfile sample)
    {
        var result = new JsonObject
        {
            ["id"] = sample.Id.Value,
            ["name"] = sample.Name,
            ["topology"] = sample.Topology.ToString(),
            ["electrodes"] = new JsonArray(sample.Electrodes.Select(e =>
            {
                var electrode = new JsonObject { ["label"] = e.Label.Value, ["channel"] = e.Channel.Value, ["connection"] = e.Connection.Value, ["enabled"] = e.Enabled };
                OptionalText(electrode, "notes", e.Notes); return (JsonNode)electrode;
            }).ToArray()),
        };
        OptionalText(result, "originalId", sample.OriginalId); OptionalText(result, "material", sample.Material);
        OptionalText(result, "substrate", sample.Substrate); OptionalText(result, "notes", sample.Notes);
        if (sample.Geometry != SampleGeometry.Empty)
            result["geometry"] = new JsonObject
            {
                ["width"] = sample.Geometry.Width is { } w ? EncodeQuantity(w) : null,
                ["length"] = sample.Geometry.Length is { } l ? EncodeQuantity(l) : null,
                ["thickness"] = sample.Geometry.Thickness is { } t ? EncodeQuantity(t) : null,
            };
        if (sample.Geometry.Spacing is { } spacing) ((JsonObject)result["geometry"]!)["spacing"] = EncodeQuantity(spacing);
        return result;
    }
    private static RoutingConfiguration DecodeRouting(JsonNode node)
    {
        var o = Object(node, "matrix", "channels", "wirings", "samples");
        var channels = Array(Required(o, "channels")).Select(v =>
        {
            var c = Object(v, "id", "name", "connections", "originalId", "groupIndex", "alias", "notes");
            return new ChannelProfile(new(Text(c, "id")), Text(c, "name"), Array(Required(c, "connections")).Select(i =>
            {
                var connection = Object(i, "key", "slot", "column", "enabled", "index", "label", "displayRow", "raw", "notes");
                return new ChannelConnection(new(Text(connection, "key")), connection["slot"]?.GetValue<int>(), connection["column"]?.GetValue<int>(), Boolean(connection, "enabled"), connection["index"]?.GetValue<int>(), ProfileText(connection, "label"), connection["displayRow"]?.GetValue<int>(), ProfileText(connection, "raw"), ProfileText(connection, "notes"));
            }), ProfileText(c, "originalId"), c["groupIndex"]?.GetValue<int>(), ProfileText(c, "alias"), ProfileText(c, "notes"));
        });
        var wirings = Array(Required(o, "wirings")).Select(v =>
        {
            var w = Object(v, "id", "sourceMeter", "sourcePlus", "sourceMinus", "sensePlus", "senseMinus", "label");
            return new InstrumentWiringProfile(new(Text(w, "id")), new(Text(w, "sourceMeter")), DecodePort(w["sourcePlus"]), DecodePort(w["sourceMinus"]), DecodePort(w["sensePlus"]), DecodePort(w["senseMinus"]), ProfileText(w, "label"));
        });
        var samples = Array(Required(o, "samples")).Select(v =>
        {
            var s = Object(v, "id", "name", "topology", "electrodes", "geometry", "originalId", "material", "substrate", "notes");
            SampleGeometry? geometry = null;
            if (s.ContainsKey("geometry"))
            {
                var g = Object(Required(s, "geometry"), "width", "length", "thickness", "spacing");
                geometry = new(g["width"] is { } w ? DecodeQuantity(w) : null, g["length"] is { } l ? DecodeQuantity(l) : null, g["thickness"] is { } t ? DecodeQuantity(t) : null, g["spacing"] is { } space ? DecodeQuantity(space) : null);
            }
            return new SampleProfile(new(Text(s, "id")), Text(s, "name"), NamedEnum<SampleTopology>(s, "topology"), Array(Required(s, "electrodes")).Select(i =>
            {
                var e = Object(i, "label", "channel", "connection", "enabled", "notes");
                return new SampleElectrode(new(Text(e, "label")), new(Text(e, "channel")), new(Text(e, "connection")), Boolean(e, "enabled"), ProfileText(e, "notes"));
            }), geometry, ProfileText(s, "originalId"), ProfileText(s, "material"), ProfileText(s, "substrate"), ProfileText(s, "notes"));
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

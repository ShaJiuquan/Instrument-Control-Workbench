using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeSr830AcFrame(ReadSr830AcFrameStep step, JsonObject node)
    {
        node["kind"] = "read_sr830_ac_frame"; node["device"] = step.Device.Value; node["isolator"] = step.Isolator.Value;
        node["connections"] = new JsonArray(step.Connections.Select(point => (JsonNode)new JsonObject
        { ["slot"] = point.Slot, ["row"] = point.Row, ["column"] = point.Column }).ToArray());
        node["frequency"] = EncodeQuantity(step.Excitation.Frequency); node["sineAmplitude"] = EncodeQuantity(step.Excitation.SineAmplitude);
        node["harmonic"] = step.Excitation.Harmonic; node["input"] = step.Input.ToString(); node["prefix"] = step.Prefix.Value;
        node["settleSeconds"] = (decimal)step.Settle.Ticks / TimeSpan.TicksPerSecond;
        return node;
    }

    private static ReadSr830AcFrameStep DecodeSr830AcFrame(JsonObject node, StepId id)
    {
        Object(node, "id", "kind", "device", "isolator", "connections", "frequency", "sineAmplitude", "harmonic", "input", "prefix", "settleSeconds");
        var points = Array(Required(node, "connections"));
        if (points.Count is < 1 or > 4096) throw new FormatException("SR830 AC connections require one to 4096 entries.");
        var ticks = Required(node, "settleSeconds").GetValue<decimal>() * TimeSpan.TicksPerSecond;
        if (ticks < 0 || ticks > (decimal)(uint.MaxValue - 1) * TimeSpan.TicksPerMillisecond || ticks != decimal.Truncate(ticks))
            throw new FormatException("SR830 AC settling must be nonnegative, within the timer range and exact to runtime ticks.");
        return new(id, new(Text(node, "device")), new(Text(node, "isolator")), points.Select(value =>
        {
            var point = Object(value, "slot", "row", "column");
            return new MatrixCrossPoint(Integer(point, "slot"), Integer(point, "row"), Integer(point, "column"));
        }), new Sr830ExcitationSettings(DecodeQuantity(Required(node, "frequency")), DecodeQuantity(Required(node, "sineAmplitude")), Integer(node, "harmonic")),
            NamedEnum<Sr830InputMode>(node, "input"), new(Text(node, "prefix")), TimeSpan.FromTicks((long)ticks));
    }
}

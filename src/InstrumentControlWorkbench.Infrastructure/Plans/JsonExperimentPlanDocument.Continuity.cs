using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeContinuity(ReadElectrodeContinuityStep read, JsonObject o)
    {
        o["kind"] = "read_electrode_continuity"; o["configuration"] = EncodePreparation(read.Source);
        o["channel"] = read.Channel.Value; o["pair"] = read.Pair.ToString(); o["settleSeconds"] = read.Settle.TotalSeconds;
        o["maximumConnectedResistance"] = EncodeQuantity(read.MaximumConnectedResistance);
        o["statusColumn"] = read.StatusColumn.Value; o["reasonColumn"] = read.ReasonColumn.Value; o["voltageColumn"] = read.VoltageColumn.Value; o["currentColumn"] = read.CurrentColumn.Value; o["resistanceColumn"] = read.ResistanceColumn.Value;
        if (read.RequiredRoute is { } route) o["requiredRoute"] = new JsonObject { ["device"] = route.Device.Value, ["closed"] = new JsonArray(route.Closed.Select(point => (JsonNode)new JsonObject { ["slot"] = point.Slot, ["row"] = point.Row, ["column"] = point.Column }).ToArray()) };
        return o;
    }
    private static ReadElectrodeContinuityStep DecodeContinuity(JsonObject o, StepId id)
    {
        Object(o, "id", "kind", "configuration", "channel", "pair", "settleSeconds", "maximumConnectedResistance", "statusColumn", "reasonColumn", "voltageColumn", "currentColumn", "resistanceColumn", "requiredRoute");
        MatrixRouteExpectation? expected = null;
        if (o.ContainsKey("requiredRoute"))
        {
            var route = Object(o["requiredRoute"], "device", "closed"); expected = new(new(Text(route, "device")), Array(Required(route, "closed")).Select(p => { var point = Object(p, "slot", "row", "column"); return new MatrixCrossPoint(Integer(point, "slot"), Integer(point, "row"), Integer(point, "column")); }));
        }
        return new(id, DecodePreparation(o["configuration"]), new ChannelProfileId(Text(o, "channel")), NamedEnum<ElectrodePair>(o, "pair"), expected, Duration(o, "settleSeconds"), DecodeQuantity(o["maximumConnectedResistance"]), new(Text(o, "statusColumn")), new(Text(o, "reasonColumn")), new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), new(Text(o, "resistanceColumn")));
    }
}

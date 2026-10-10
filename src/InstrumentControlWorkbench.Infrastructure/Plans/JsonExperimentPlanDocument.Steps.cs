using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeStep(ExperimentStep step, int depth, ref int count)
    {
        CheckSize(depth, ref count);
        var o = new JsonObject { ["id"] = step.Id.Value };
        switch (step)
        {
            case SequenceStep s:
                o["kind"] = "sequence";
                var children = new JsonArray();
                foreach (var child in s.Children) children.Add(EncodeStep(child, depth + 1, ref count));
                o["children"] = children; break;
            case RepeatStep s:
                o["kind"] = "repeat"; o["cycles"] = s.Cycles; o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case ScanStep s:
                o["kind"] = "scan"; o["axis"] = EncodeAddress(s.Axis); o["column"] = s.Column.Value;
                o["values"] = EncodeValues(s.Values); o["dwellSeconds"] = s.Dwell.TotalSeconds; o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case ForEachStep s:
                o["kind"] = "for_each"; o["variable"] = s.Variable.Value; o["values"] = EncodeValues(s.Values);
                o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case SetParameterStep s:
                o["kind"] = "set"; o["address"] = EncodeAddress(s.Address); o["value"] = EncodeValue(s.Value); break;
            case AssignValueStep s:
                o["kind"] = "assign"; o["variable"] = s.Variable.Value; o["value"] = EncodeValue(s.Value); break;
            case RouteMatrixStep s:
                o["kind"] = "route_matrix"; o["device"] = s.Device.Value; o["settleSeconds"] = s.Settle.TotalSeconds;
                o["closed"] = new JsonArray(s.Closed.Select(p => (JsonNode)new JsonObject
                { ["slot"] = p.Slot, ["row"] = p.Row, ["column"] = p.Column }).ToArray()); break;
            case SourceOutputStep s:
                o["kind"] = "output"; o["device"] = s.Device.Value; o["enabled"] = s.Enabled; break;
            case DelayStep s:
                o["kind"] = "delay"; o["seconds"] = s.Duration.TotalSeconds; break;
            case ReadStep s:
                o["kind"] = "read"; o["channels"] = new JsonArray(s.Channels.Select(c => (JsonNode)new JsonObject
                { ["column"] = c.Column.Value, ["address"] = EncodeAddress(c.Address) }).ToArray()); break;
            case ReadSampleResistanceStep sample:
                var acquisition = sample.Acquisition;
                o["kind"] = "read_sample_resistance"; o["device"] = acquisition.Device.Value; o["function"] = acquisition.Function.ToString();
                o["voltageColumn"] = acquisition.VoltageColumn.Value; o["currentColumn"] = acquisition.CurrentColumn.Value;
                o["resistanceColumn"] = acquisition.ResistanceColumn.Value; o["minimumCurrent"] = EncodeQuantity(acquisition.MinimumCurrent);
                o["binding"] = EncodeBinding(sample.Binding); o["settleSeconds"] = sample.Settle.TotalSeconds;
                o["routingManagedExternally"] = sample.RoutingManagedExternally; break;
            case ReadResistanceStep s:
                o["kind"] = "read_resistance"; o["device"] = s.Device.Value; o["function"] = s.Function.ToString();
                o["voltageColumn"] = s.VoltageColumn.Value; o["currentColumn"] = s.CurrentColumn.Value;
                o["resistanceColumn"] = s.ResistanceColumn.Value; o["minimumCurrent"] = EncodeQuantity(s.MinimumCurrent); break;
            case WaitStableStep s:
                o["kind"] = "wait_stable"; o["readback"] = EncodeAddress(s.Readback); o["target"] = EncodeValue(s.Target);
                o["tolerance"] = EncodeQuantity(s.Tolerance); o["stableSeconds"] = s.StableFor.TotalSeconds;
                o["pollSeconds"] = s.PollInterval.TotalSeconds; o["timeoutSeconds"] = s.Timeout.TotalSeconds; break;
            case IfElseStep s:
                o["kind"] = "if_else"; o["condition"] = new JsonObject
                {
                    ["variable"] = s.Condition.Variable.Value,
                    ["operator"] = s.Condition.Operator.ToString(),
                    ["value"] = EncodeValue(s.Condition.Value)
                };
                o["then"] = EncodeStep(s.Then, depth + 1, ref count);
                if (s.Else is { } otherwise) o["else"] = EncodeStep(otherwise, depth + 1, ref count); break;
            default: throw new NotSupportedException($"No document codec exists for {step.GetType().Name}.");
        }
        return o;
    }

    private static ExperimentStep DecodeStep(JsonNode? node, int depth, ref int count)
    {
        CheckSize(depth, ref count);
        var o = node as JsonObject ?? throw new FormatException("A step must be an object.");
        var id = new StepId(Text(o, "id"));
        var kind = Text(o, "kind");
        switch (kind)
        {
            case "sequence":
                Object(o, "id", "kind", "children");
                var children = new List<ExperimentStep>();
                foreach (var child in Array(Required(o, "children"))) children.Add(DecodeStep(child, depth + 1, ref count));
                return new SequenceStep(id, children);
            case "repeat":
                Object(o, "id", "kind", "cycles", "body");
                return new RepeatStep(id, Integer(o, "cycles"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "scan":
                Object(o, "id", "kind", "axis", "column", "values", "dwellSeconds", "body");
                return new ScanStep(id, DecodeAddress(o["axis"]), new(Text(o, "column")), DecodeValues(o["values"]),
                    Duration(o, "dwellSeconds"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "for_each":
                Object(o, "id", "kind", "variable", "values", "body");
                return new ForEachStep(id, new(Text(o, "variable")), DecodeValues(o["values"]), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "set":
                Object(o, "id", "kind", "address", "value");
                return new SetParameterStep(id, DecodeAddress(o["address"]), DecodeValue(o["value"]));
            case "assign":
                Object(o, "id", "kind", "variable", "value");
                return new AssignValueStep(id, new(Text(o, "variable")), DecodeValue(o["value"]));
            case "route_matrix":
                Object(o, "id", "kind", "device", "closed", "settleSeconds");
                return new RouteMatrixStep(id, new(Text(o, "device")), Array(Required(o, "closed")).Select(p =>
                {
                    var point = Object(p, "slot", "row", "column");
                    return new MatrixCrossPoint(Integer(point, "slot"), Integer(point, "row"), Integer(point, "column"));
                }), Duration(o, "settleSeconds"));
            case "output":
                Object(o, "id", "kind", "device", "enabled");
                return new SourceOutputStep(id, new(Text(o, "device")), Boolean(o, "enabled"));
            case "delay":
                Object(o, "id", "kind", "seconds");
                return new DelayStep(id, Duration(o, "seconds"));
            case "read":
                Object(o, "id", "kind", "channels");
                return new ReadStep(id, Array(Required(o, "channels")).Select(c =>
                {
                    var channel = Object(c, "column", "address");
                    return new ReadChannel(new(Text(channel, "column")), DecodeAddress(channel["address"]));
                }));
            case "read_sample_resistance":
                Object(o, "id", "kind", "device", "function", "voltageColumn", "currentColumn", "resistanceColumn", "minimumCurrent", "binding", "settleSeconds", "routingManagedExternally");
                return new ReadSampleResistanceStep(new(id, new(Text(o, "device")), NamedEnum<SourceFunction>(o, "function"),
                    new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), new(Text(o, "resistanceColumn")), DecodeQuantity(o["minimumCurrent"])),
                    DecodeBinding(o["binding"]), Duration(o, "settleSeconds"), Boolean(o, "routingManagedExternally"));
            case "read_resistance":
                Object(o, "id", "kind", "device", "function", "voltageColumn", "currentColumn", "resistanceColumn", "minimumCurrent");
                return new ReadResistanceStep(id, new(Text(o, "device")), NamedEnum<SourceFunction>(o, "function"),
                    new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), new(Text(o, "resistanceColumn")), DecodeQuantity(o["minimumCurrent"]));
            case "wait_stable":
                Object(o, "id", "kind", "readback", "target", "tolerance", "stableSeconds", "pollSeconds", "timeoutSeconds");
                return new WaitStableStep(id, DecodeAddress(o["readback"]), DecodeValue(o["target"]), DecodeQuantity(o["tolerance"]),
                    Duration(o, "stableSeconds"), Duration(o, "pollSeconds"), Duration(o, "timeoutSeconds"));
            case "if_else":
                Object(o, "id", "kind", "condition", "then", "else");
                var condition = Object(o["condition"], "variable", "operator", "value");
                var then = DecodeStep(Required(o, "then"), depth + 1, ref count);
                var otherwise = o.ContainsKey("else") ? DecodeStep(Required(o, "else"), depth + 1, ref count) : null;
                return new IfElseStep(id, new(new(Text(condition, "variable")), NamedEnum<ComparisonOperator>(condition, "operator"),
                    DecodeValue(condition["value"])), then, otherwise);
            default: throw new NotSupportedException($"Unsupported plan step '{kind}'.");
        }
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
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
            case TemperatureStabilityStep settings:
                o["kind"] = "temperature_stability"; o["device"] = settings.Device.Value;
                if (settings.Change.Tolerance is { } tolerance) o["tolerance"] = EncodeQuantity(tolerance);
                if (settings.Change.SettlingThreshold is { } settling) o["settlingThreshold"] = EncodeQuantity(settling);
                if (settings.Change.StableFor is { } stableFor) o["stableSeconds"] = stableFor.TotalSeconds; break;
            case WaitTemperatureStep wait:
                o["kind"] = "wait_temperature"; o["device"] = wait.Device.Value; o["target"] = EncodeValue(wait.Target); o["mode"] = wait.Mode.ToString();
                o["pollSeconds"] = wait.PollInterval.TotalSeconds; o["timeoutSeconds"] = wait.Timeout.TotalSeconds; break;
            case TemperatureControlStep command:
                o["kind"] = "temperature_control"; o["device"] = command.Device.Value; o["action"] = command.Action.ToString();
                if (command.Rate is { } commandRate) o["rate"] = EncodeQuantity(commandRate); break;
            case HardwareTemperatureSetpointStep hardwareTemperature:
                o["kind"] = "hardware_temperature_setpoint"; o["device"] = hardwareTemperature.Device.Value; o["target"] = EncodeValue(hardwareTemperature.Target); o["rate"] = EncodeQuantity(hardwareTemperature.Rate);
                if (!hardwareTemperature.EnableControl) o["enableControl"] = false; break;
            case SoftwareTemperatureRampStep temperature:
                o["kind"] = "software_temperature_ramp"; o["device"] = temperature.Device.Value; o["target"] = EncodeValue(temperature.Target);
                if (temperature.Rate is { } rate) o["rate"] = EncodeQuantity(rate);
                if (!temperature.EnableControl) o["enableControl"] = false; break;
            case ReadSourceSenseStep pair:
                o["kind"] = "read_source_sense"; o["device"] = pair.Device.Value; o["function"] = pair.Function.ToString();
                o["voltageColumn"] = pair.VoltageColumn.Value; o["currentColumn"] = pair.CurrentColumn.Value; EncodeEnvironment(o, pair.EnvironmentReads); break;
            case MeasurementGroupStep point:
                o["kind"] = "measurement_group"; var members = new JsonArray();
                foreach (var member in point.Members) members.Add(new JsonObject { ["label"] = member.Label, ["body"] = EncodeStep(member.Body, depth + 1, ref count) });
                o["members"] = members; break;
            case SampleAnalysisStep group:
                o["kind"] = "sample_analysis";
                var readings = new JsonArray();
                foreach (var read in group.Readings) readings.Add(EncodeStep(read, depth + 1, ref count));
                o["readings"] = readings;
                o["columns"] = new JsonArray(group.Columns.Select(c => (JsonNode)new JsonObject { ["kind"] = c.Kind.ToString(), ["column"] = c.Column.Value }).ToArray());
                break;
            case SequenceStep s:
                o["kind"] = "sequence";
                var children = new JsonArray();
                foreach (var child in s.Children) children.Add(EncodeStep(child, depth + 1, ref count));
                o["children"] = children; break;
            case ConditionStep s:
                o["kind"] = "condition"; o["variable"] = s.Condition.Variable.Value; o["operator"] = s.Condition.Operator.ToString(); o["value"] = EncodeValue(s.Condition.Value); break;
            case FallbackStep s:
                o["kind"] = "fallback"; var branches = new JsonArray();
                foreach (var child in s.Children) branches.Add(EncodeStep(child, depth + 1, ref count));
                o["children"] = branches; break;
            case RetryStep s:
                o["kind"] = "retry"; o["maxAttempts"] = s.MaxAttempts; o["delaySeconds"] = s.Delay.TotalSeconds; o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case TimeoutStep s:
                o["kind"] = "timeout"; o["timeoutSeconds"] = s.Timeout.TotalSeconds; o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case ResultDecoratorStep s:
                o["kind"] = "result_decorator"; o["effect"] = s.Effect.ToString(); o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case RepeatStep s:
                o["kind"] = "repeat"; o["cycles"] = s.Cycles; o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case ScanStep s:
                o["kind"] = "scan"; o["axis"] = EncodeAddress(s.Axis); o["column"] = s.Column.Value;
                o["values"] = EncodeValues(s.Values); o["dwellSeconds"] = s.Dwell.TotalSeconds; o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case ForEachStep s:
                o["kind"] = "for_each"; o["variable"] = s.Variable.Value; o["values"] = EncodeValues(s.Values);
                if (s.Timing != IterationTiming.Immediate) o["timing"] = s.Timing.ToString();
                o["body"] = EncodeStep(s.Body, depth + 1, ref count); break;
            case SetParameterStep s:
                o["kind"] = "set"; o["address"] = EncodeAddress(s.Address); o["value"] = EncodeValue(s.Value); break;
            case AssignValueStep s:
                o["kind"] = "assign"; o["variable"] = s.Variable.Value; o["value"] = EncodeValue(s.Value); break;
            case VerifyMatrixRouteStep s:
                o["kind"] = "verify_matrix_route"; o["device"] = s.Device.Value;
                o["closed"] = new JsonArray(s.Closed.Select(p => (JsonNode)new JsonObject
                { ["slot"] = p.Slot, ["row"] = p.Row, ["column"] = p.Column }).ToArray()); break;
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
                { ["column"] = c.Column.Value, ["address"] = EncodeAddress(c.Address) }).ToArray()); EncodeEnvironment(o, s.EnvironmentReads); break;
            case ReadCurrentResistanceStep independent:
                o["kind"] = "read_current_resistance"; o["source"] = independent.Source.Value; o["meter"] = independent.Meter.Value; o["currentReference"] = independent.CurrentReference.ToString();
                o["voltageColumn"] = independent.VoltageColumn.Value; o["currentColumn"] = independent.CurrentColumn.Value; o["resistanceColumn"] = independent.ResistanceColumn.Value;
                o["minimumCurrent"] = EncodeQuantity(independent.MinimumCurrent); EncodeEnvironment(o, independent.EnvironmentReads);
                if (independent.Retry is { } retry) o["retry"] = new JsonObject { ["ownsSourceOutput"] = retry.OwnsSourceOutput, ["settleSeconds"] = retry.Settle.TotalSeconds };
                if (independent.Retry is { ContinueOnAcquisitionFailure: true }) o["retry"]!["continueOnAcquisitionFailure"] = true;
                if (independent.ExpectedProgrammedCurrent is { } expected) o["expectedProgrammedCurrent"] = EncodeValue(expected);
                if (independent.RequiredRoute is { } required) o["requiredRoute"] = new JsonObject { ["device"] = required.Device.Value, ["closed"] = new JsonArray(required.Closed.Select(p => (JsonNode)new JsonObject { ["slot"] = p.Slot, ["row"] = p.Row, ["column"] = p.Column }).ToArray()) };
                break;
            case ReadSampleResistanceStep sample:
                var acquisition = sample.Acquisition;
                if (acquisition is ReadCurrentResistanceStep)
                {
                    o["kind"] = "read_independent_sample_resistance";
                    o["acquisition"] = EncodeStep(acquisition, depth + 1, ref count);
                    o["binding"] = EncodeBinding(sample.Binding); o["settleSeconds"] = sample.Settle.TotalSeconds;
                    o["acquisitionSettleSeconds"] = sample.AcquisitionSettle.TotalSeconds;
                    break;
                }
                o["kind"] = "read_sample_resistance"; o["device"] = acquisition.Device.Value; o["function"] = acquisition.Function.ToString();
                o["voltageColumn"] = acquisition.VoltageColumn.Value; o["currentColumn"] = acquisition.CurrentColumn.Value;
                o["resistanceColumn"] = acquisition.ResistanceColumn.Value; o["minimumCurrent"] = EncodeQuantity(acquisition.MinimumCurrent);
                o["binding"] = EncodeBinding(sample.Binding); o["settleSeconds"] = sample.Settle.TotalSeconds;
                o["routingManagedExternally"] = sample.RoutingManagedExternally;
                EncodeEnvironment(o, acquisition.EnvironmentReads);
                if (sample.AcquisitionSettle != TimeSpan.Zero) o["acquisitionSettleSeconds"] = sample.AcquisitionSettle.TotalSeconds; break;
            case ReadResistanceStep s:
                o["kind"] = "read_resistance"; o["device"] = s.Device.Value; o["function"] = s.Function.ToString();
                o["voltageColumn"] = s.VoltageColumn.Value; o["currentColumn"] = s.CurrentColumn.Value;
                o["resistanceColumn"] = s.ResistanceColumn.Value; o["minimumCurrent"] = EncodeQuantity(s.MinimumCurrent); EncodeEnvironment(o, s.EnvironmentReads); break;
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
            case "temperature_stability":
                Object(o, "id", "kind", "device", "tolerance", "settlingThreshold", "stableSeconds");
                return new TemperatureStabilityStep(id, new(Text(o, "device")), new(
                    o.ContainsKey("tolerance") ? DecodeQuantity(o["tolerance"]) : null,
                    o.ContainsKey("settlingThreshold") ? DecodeQuantity(o["settlingThreshold"]) : null,
                    o.ContainsKey("stableSeconds") ? Duration(o, "stableSeconds") : null));
            case "wait_temperature":
                Object(o, "id", "kind", "device", "target", "mode", "pollSeconds", "timeoutSeconds");
                return new WaitTemperatureStep(id, new(Text(o, "device")), DecodeValue(o["target"]), NamedEnum<TemperatureWaitMode>(o, "mode"), Duration(o, "pollSeconds"), Duration(o, "timeoutSeconds"));
            case "temperature_control":
                Object(o, "id", "kind", "device", "action", "rate");
                return new TemperatureControlStep(id, new(Text(o, "device")), NamedEnum<TemperatureControlAction>(o, "action"), o.ContainsKey("rate") ? DecodeQuantity(o["rate"]) : null);
            case "hardware_temperature_setpoint":
                Object(o, "id", "kind", "device", "target", "rate", "enableControl");
                return new HardwareTemperatureSetpointStep(id, new(Text(o, "device")), DecodeValue(o["target"]), DecodeQuantity(o["rate"]), !o.ContainsKey("enableControl") || Boolean(o, "enableControl"));
            case "software_temperature_ramp":
                Object(o, "id", "kind", "device", "target", "rate", "enableControl");
                return new SoftwareTemperatureRampStep(id, new(Text(o, "device")), DecodeValue(o["target"]), o.ContainsKey("rate") ? DecodeQuantity(o["rate"]) : null, !o.ContainsKey("enableControl") || Boolean(o, "enableControl"));
            case "read_source_sense":
                Object(o, "id", "kind", "device", "function", "voltageColumn", "currentColumn", "environmentReads");
                return new ReadSourceSenseStep(id, new(Text(o, "device")), NamedEnum<SourceFunction>(o, "function"), new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), DecodeEnvironment(o));
            case "measurement_group":
                Object(o, "id", "kind", "members"); var pointMembers = new List<MeasurementGroupMember>();
                foreach (var value in Array(Required(o, "members")))
                {
                    var member = Object(value, "label", "body");
                    pointMembers.Add(new(Text(member, "label"), DecodeStep(Required(member, "body"), depth + 1, ref count)));
                }
                return new MeasurementGroupStep(id, pointMembers);
            case "sample_analysis":
                Object(o, "id", "kind", "readings", "columns");
                var groupReadings = new List<ReadSampleResistanceStep>();
                foreach (var child in Array(Required(o, "readings")))
                    groupReadings.Add(DecodeStep(child, depth + 1, ref count) as ReadSampleResistanceStep ?? throw new FormatException("Analysis children must be sample-resistance readings."));
                return new SampleAnalysisStep(id, groupReadings, Array(Required(o, "columns")).Select(c =>
                {
                    var column = Object(c, "kind", "column"); return new SampleDerivedColumn(NamedEnum<SampleDerivedQuantity>(column, "kind"), new(Text(column, "column")));
                }));
            case "sequence":
                Object(o, "id", "kind", "children");
                var children = new List<ExperimentStep>();
                foreach (var child in Array(Required(o, "children"))) children.Add(DecodeStep(child, depth + 1, ref count));
                return new SequenceStep(id, children);
            case "condition":
                Object(o, "id", "kind", "variable", "operator", "value");
                return new ConditionStep(id, new(new(Text(o, "variable")), NamedEnum<ComparisonOperator>(o, "operator"), DecodeValue(o["value"])));
            case "fallback":
                Object(o, "id", "kind", "children"); var branches = new List<ExperimentStep>();
                foreach (var child in Array(o["children"])) branches.Add(DecodeStep(child ?? throw new FormatException("Null fallback branch."), depth + 1, ref count));
                return new FallbackStep(id, branches);
            case "retry":
                Object(o, "id", "kind", "maxAttempts", "delaySeconds", "body");
                return new RetryStep(id, Integer(o, "maxAttempts"), Duration(o, "delaySeconds"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "timeout":
                Object(o, "id", "kind", "timeoutSeconds", "body");
                return new TimeoutStep(id, Duration(o, "timeoutSeconds"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "result_decorator":
                Object(o, "id", "kind", "effect", "body");
                return new ResultDecoratorStep(id, NamedEnum<ResultEffect>(o, "effect"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "repeat":
                Object(o, "id", "kind", "cycles", "body");
                return new RepeatStep(id, Integer(o, "cycles"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "scan":
                Object(o, "id", "kind", "axis", "column", "values", "dwellSeconds", "body");
                return new ScanStep(id, DecodeAddress(o["axis"]), new(Text(o, "column")), DecodeValues(o["values"]),
                    Duration(o, "dwellSeconds"), DecodeStep(Required(o, "body"), depth + 1, ref count));
            case "for_each":
                Object(o, "id", "kind", "variable", "values", "body", "timing");
                return new ForEachStep(id, new(Text(o, "variable")), DecodeValues(o["values"]), DecodeStep(Required(o, "body"), depth + 1, ref count),
                    o.ContainsKey("timing") ? NamedEnum<IterationTiming>(o, "timing") : IterationTiming.Immediate);
            case "set":
                Object(o, "id", "kind", "address", "value");
                return new SetParameterStep(id, DecodeAddress(o["address"]), DecodeValue(o["value"]));
            case "assign":
                Object(o, "id", "kind", "variable", "value");
                return new AssignValueStep(id, new(Text(o, "variable")), DecodeValue(o["value"]));
            case "verify_matrix_route":
                Object(o, "id", "kind", "device", "closed");
                return new VerifyMatrixRouteStep(id, new(Text(o, "device")), Array(Required(o, "closed")).Select(p =>
                {
                    var point = Object(p, "slot", "row", "column");
                    return new MatrixCrossPoint(Integer(point, "slot"), Integer(point, "row"), Integer(point, "column"));
                }));
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
                Object(o, "id", "kind", "channels", "environmentReads");
                return new ReadStep(id, Array(Required(o, "channels")).Select(c =>
                {
                    var channel = Object(c, "column", "address");
                    return new ReadChannel(new(Text(channel, "column")), DecodeAddress(channel["address"]));
                }), DecodeEnvironment(o));
            case "read_current_resistance":
                Object(o, "id", "kind", "source", "meter", "currentReference", "voltageColumn", "currentColumn", "resistanceColumn", "minimumCurrent", "expectedProgrammedCurrent", "requiredRoute", "environmentReads", "retry");
                MatrixRouteExpectation? expectation = null;
                if (o.ContainsKey("requiredRoute"))
                {
                    var route = Object(o["requiredRoute"], "device", "closed");
                    expectation = new(new(Text(route, "device")), Array(Required(route, "closed")).Select(p => { var point = Object(p, "slot", "row", "column"); return new MatrixCrossPoint(Integer(point, "slot"), Integer(point, "row"), Integer(point, "column")); }));
                }
                CurrentResistanceRetry? retryPolicy = null;
                if (o.ContainsKey("retry"))
                {
                    var retryObject = Object(o["retry"], "ownsSourceOutput", "settleSeconds", "continueOnAcquisitionFailure");
                    retryPolicy = new(Boolean(retryObject, "ownsSourceOutput"), Duration(retryObject, "settleSeconds"), retryObject.ContainsKey("continueOnAcquisitionFailure") && Boolean(retryObject, "continueOnAcquisitionFailure"));
                }
                return new ReadCurrentResistanceStep(id, new(Text(o, "source")), new(Text(o, "meter")), NamedEnum<CurrentReference>(o, "currentReference"),
                    new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), new(Text(o, "resistanceColumn")), DecodeQuantity(o["minimumCurrent"]),
                    o.ContainsKey("expectedProgrammedCurrent") ? DecodeValue(o["expectedProgrammedCurrent"]) : null, expectation, DecodeEnvironment(o), retryPolicy);
            case "read_independent_sample_resistance":
                Object(o, "id", "kind", "acquisition", "binding", "settleSeconds", "acquisitionSettleSeconds");
                var independentSample = DecodeStep(Required(o, "acquisition"), depth + 1, ref count) as ReadCurrentResistanceStep ?? throw new FormatException("Independent sample needs its current-source/meter acquisition.");
                if (independentSample.Id != id) throw new FormatException("Sample and nested acquisition IDs must match.");
                return new ReadSampleResistanceStep(independentSample, DecodeBinding(o["binding"]), Duration(o, "settleSeconds"), acquisitionSettle: Duration(o, "acquisitionSettleSeconds"));
            case "read_sample_resistance":
                Object(o, "id", "kind", "device", "function", "voltageColumn", "currentColumn", "resistanceColumn", "minimumCurrent", "binding", "settleSeconds", "routingManagedExternally", "acquisitionSettleSeconds", "environmentReads");
                return new ReadSampleResistanceStep(new ReadResistanceStep(id, new(Text(o, "device")), NamedEnum<SourceFunction>(o, "function"),
                    new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), new(Text(o, "resistanceColumn")), DecodeQuantity(o["minimumCurrent"]), DecodeEnvironment(o)),
                    DecodeBinding(o["binding"]), Duration(o, "settleSeconds"), Boolean(o, "routingManagedExternally"), o.ContainsKey("acquisitionSettleSeconds") ? Duration(o, "acquisitionSettleSeconds") : TimeSpan.Zero);
            case "read_resistance":
                Object(o, "id", "kind", "device", "function", "voltageColumn", "currentColumn", "resistanceColumn", "minimumCurrent", "environmentReads");
                return new ReadResistanceStep(id, new(Text(o, "device")), NamedEnum<SourceFunction>(o, "function"),
                    new(Text(o, "voltageColumn")), new(Text(o, "currentColumn")), new(Text(o, "resistanceColumn")), DecodeQuantity(o["minimumCurrent"]), DecodeEnvironment(o));
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

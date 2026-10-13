using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeCooling(TemperatureCoolingStep step, JsonObject o, int depth, ref int count)
    {
        o["kind"] = "temperature_cooling"; o["device"] = step.Device.Value; o["column"] = step.Column.Value; o["thresholds"] = EncodeValues(step.Thresholds);
        o["coolingTarget"] = EncodeQuantity(step.CoolingTarget); o["endTemperature"] = EncodeQuantity(step.EndTemperature); o["tolerance"] = EncodeQuantity(step.Tolerance);
        o["timeoutSeconds"] = step.Timeout.TotalSeconds; o["pollSeconds"] = step.PollInterval.TotalSeconds; o["sampling"] = step.Sampling.ToString(); o["intervalSeconds"] = step.Interval.TotalSeconds;
        o["body"] = EncodeStep(step.Body, depth + 1, ref count); return o;
    }
    private static TemperatureCoolingStep DecodeCooling(JsonObject o, StepId id, int depth, ref int count)
    {
        Object(o, "id", "kind", "device", "column", "thresholds", "coolingTarget", "endTemperature", "tolerance", "timeoutSeconds", "pollSeconds", "sampling", "intervalSeconds", "body");
        return new(id, new(Text(o, "device")), new(Text(o, "column")), DecodeValues(Required(o, "thresholds")), DecodeQuantity(Required(o, "coolingTarget")), DecodeQuantity(Required(o, "endTemperature")), DecodeQuantity(Required(o, "tolerance")), Duration(o, "timeoutSeconds"), Duration(o, "pollSeconds"), NamedEnum<CoolingSamplingMode>(o, "sampling"), Duration(o, "intervalSeconds"), DecodeStep(Required(o, "body"), depth + 1, ref count));
    }
}

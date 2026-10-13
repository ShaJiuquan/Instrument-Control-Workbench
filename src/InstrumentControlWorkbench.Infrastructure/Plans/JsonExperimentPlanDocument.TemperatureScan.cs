using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeHardwareScan(HardwareTemperatureScanStep scan, JsonObject o, int depth, ref int count)
    {
        o["kind"] = "hardware_temperature_scan"; o["device"] = scan.Device.Value; o["column"] = scan.Column.Value; o["points"] = EncodeValues(scan.Points); o["rate"] = EncodeQuantity(scan.Rate); o["waitStableFirst"] = scan.WaitStableFirst;
        o["tolerance"] = EncodeQuantity(scan.Tolerance); o["stableCount"] = scan.StableCount; o["stableTimeoutSeconds"] = scan.StableTimeout.TotalSeconds; o["stepTimeoutSeconds"] = scan.StepTimeout.TotalSeconds; o["pollSeconds"] = scan.PollInterval.TotalSeconds; o["sampling"] = scan.Sampling.ToString(); o["intervalSeconds"] = scan.Interval.TotalSeconds; o["body"] = EncodeStep(scan.Body, depth + 1, ref count); return o;
    }
    private static HardwareTemperatureScanStep DecodeHardwareScan(JsonObject o, StepId id, int depth, ref int count)
    {
        Object(o, "id", "kind", "device", "column", "points", "rate", "waitStableFirst", "tolerance", "stableCount", "stableTimeoutSeconds", "stepTimeoutSeconds", "pollSeconds", "sampling", "intervalSeconds", "body");
        return new(id, new(Text(o, "device")), new(Text(o, "column")), DecodeValues(Required(o, "points")), DecodeQuantity(Required(o, "rate")), Boolean(o, "waitStableFirst"), DecodeQuantity(Required(o, "tolerance")), Integer(o, "stableCount"), Duration(o, "stableTimeoutSeconds"), Duration(o, "stepTimeoutSeconds"), Duration(o, "pollSeconds"), NamedEnum<TemperatureRampSampling>(o, "sampling"), Duration(o, "intervalSeconds"), DecodeStep(Required(o, "body"), depth + 1, ref count));
    }
}

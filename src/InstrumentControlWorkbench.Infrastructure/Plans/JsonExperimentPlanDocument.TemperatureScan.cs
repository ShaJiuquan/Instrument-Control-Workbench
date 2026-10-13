using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument
{
    private static JsonObject EncodeTemperatureScan(TemperatureScanStep scan, JsonObject o, int depth, ref int count)
    {
        o["kind"] = scan is SoftwareTemperatureScanStep ? "software_temperature_scan" : "hardware_temperature_scan"; o["device"] = scan.Device.Value; o["column"] = scan.Column.Value; o["points"] = EncodeValues(scan.Points); o["rate"] = EncodeQuantity(scan.Rate); o["waitStableFirst"] = scan.WaitStableFirst;
        o["tolerance"] = EncodeQuantity(scan.Tolerance); o["stableCount"] = scan.StableCount; o["stableTimeoutSeconds"] = scan.StableTimeout.TotalSeconds; o["stepTimeoutSeconds"] = scan.StepTimeout.TotalSeconds; o["pollSeconds"] = scan.PollInterval.TotalSeconds; o["sampling"] = scan.Sampling.ToString(); o["intervalSeconds"] = scan.Interval.TotalSeconds; o["body"] = EncodeStep(scan.Body, depth + 1, ref count); return o;
    }
    private static TemperatureScanStep DecodeTemperatureScan(JsonObject o, StepId id, int depth, ref int count, bool software)
    {
        Object(o, "id", "kind", "device", "column", "points", "rate", "waitStableFirst", "tolerance", "stableCount", "stableTimeoutSeconds", "stepTimeoutSeconds", "pollSeconds", "sampling", "intervalSeconds", "body");
        var device = new InstrumentId(Text(o, "device"));
        var column = new ParameterId(Text(o, "column"));
        var points = DecodeValues(Required(o, "points")); var rate = DecodeQuantity(Required(o, "rate")); var wait = Boolean(o, "waitStableFirst");
        var tolerance = DecodeQuantity(Required(o, "tolerance")); var stableCount = Integer(o, "stableCount");
        var stableTimeout = Duration(o, "stableTimeoutSeconds"); var stepTimeout = Duration(o, "stepTimeoutSeconds"); var poll = Duration(o, "pollSeconds");
        var sampling = NamedEnum<TemperatureRampSampling>(o, "sampling"); var interval = Duration(o, "intervalSeconds"); var body = DecodeStep(Required(o, "body"), depth + 1, ref count);
        return software ? new SoftwareTemperatureScanStep(id, device, column, points, rate, wait, tolerance, stableCount, stableTimeout, stepTimeout, poll, sampling, interval, body)
            : new HardwareTemperatureScanStep(id, device, column, points, rate, wait, tolerance, stableCount, stableTimeout, stepTimeout, poll, sampling, interval, body);
    }
}

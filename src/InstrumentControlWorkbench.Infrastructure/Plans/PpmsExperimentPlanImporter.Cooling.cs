using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static bool IsCoolingMode(string mode) => mode.Trim().ToLowerInvariant() is "cooling" or "temperature_cooling" or "temp_cooling" or "scan_temp_cooling";
    private static ExperimentStep CoolingScan(JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        Fields(p, "points", "start", "end", "step", "points_mode", "scan_mode", "sample_mode", "cooling_setpoint_k", "end_temp_k", "tolerance_k", "timeout_s", "step_timeout_s", "poll_interval_ms", "interval_s");
        if (p["scan_mode"] is not null && !IsCoolingMode(Text(p, "scan_mode"))) throw new FormatException("Cooling scan mode conflicts with the scan name.");
        if (p.ContainsKey("timeout_s") && p.ContainsKey("step_timeout_s")) throw new FormatException("Choose one cooling timeout alias.");
        var sampling = Text(p, "sample_mode", "linear").Trim().ToLowerInvariant() switch
        {
            "time" or "timed" or "interval" => CoolingSamplingMode.Timed,
            "immediate" or "continuous" => CoolingSamplingMode.Immediate,
            "linear" or "log" or "inv_t" or "point" or "setpoint" or "none" or "no_wait" => CoolingSamplingMode.Threshold,
            _ => throw new FormatException("Unknown cooling sample mode."),
        };
        var axis = Axis("temperature", c); var points = Points(p["points"] is JsonObject or JsonArray ? p["points"]! : p, PhysicalUnit.Kelvin, c, 0, p["points"] is not (JsonObject or JsonArray));
        var last = points.Values[^1].Value; var end = new Quantity(Number(p, "end_temp_k", last), PhysicalUnit.Kelvin); var column = new ParameterId(path + ".axis");
        var body = new SequenceStep(new(path + ".body"), [.. ScanAxisVariables("scan_temperature", column, path), .. ScanChildren(node, path, c, depth)]);
        return new TemperatureCoolingStep(new(path), axis.Device.Id, column, points, new(Number(p, "cooling_setpoint_k", last), PhysicalUnit.Kelvin), end, new(Number(p, "tolerance_k", .05), PhysicalUnit.Kelvin), Duration(p, p.ContainsKey("step_timeout_s") ? "step_timeout_s" : "timeout_s", 3600), TimeSpan.FromMilliseconds(Integer(p, "poll_interval_ms", 500)), sampling, Duration(p, "interval_s", 30), body);
    }
}

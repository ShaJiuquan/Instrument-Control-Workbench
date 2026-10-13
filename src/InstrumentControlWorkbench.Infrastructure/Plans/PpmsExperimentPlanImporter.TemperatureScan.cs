using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static bool IsTemperatureRampMode(string mode) => mode.Trim().ToLowerInvariant() is "ramp" or "temperature_ramp" or "temp_ramp" or "scan_temp_ramp";
    private static ExperimentStep TemperatureRampScan(JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        Fields(p, "points", "start", "end", "step", "points_mode", "scan_mode", "ramp_mode", "rate_k_per_min", "wait_stable_first_point", "tolerance_k", "stable_count", "stable_timeout_s", "timeout_s", "step_timeout_s", "poll_interval_ms", "sample_mode", "interval_s");
        var mode = Text(p, "ramp_mode", "software").Trim().ToLowerInvariant();
        if (mode is not ("software" or "hardware")) throw new FormatException("Unknown temperature ramp mode.");
        if (p["scan_mode"] is not null && !IsTemperatureRampMode(Text(p, "scan_mode"))) throw new FormatException("Temperature ramp scan mode conflicts with its name.");
        if (p.ContainsKey("timeout_s") && p.ContainsKey("step_timeout_s")) throw new FormatException("Choose one ramp timeout alias.");
        var sampling = Text(p, "sample_mode", "linear").Trim().ToLowerInvariant() switch
        {
            "linear" or "log" or "inv_t" or "point" => TemperatureRampSampling.Threshold,
            "setpoint" or "none" or "no_wait" => TemperatureRampSampling.EstimatedSetpoint,
            "time" or "timed" or "interval" => TemperatureRampSampling.Timed,
            "immediate" or "continuous" => TemperatureRampSampling.Immediate,
            _ => throw new FormatException("Unknown ramp sampling mode."),
        };
        var axis = Axis("temperature", c); var points = Points(p["points"] is JsonObject or JsonArray ? p["points"]! : p, PhysicalUnit.Kelvin, c, 0, p["points"] is not (JsonObject or JsonArray)); var column = new ParameterId(path + ".axis");
        var body = new SequenceStep(new(path + ".body"), [.. ScanAxisVariables("scan_temperature", column, path), .. ScanChildren(node, path, c, depth)]);
        return mode == "software" ? new SoftwareTemperatureScanStep(new(path), axis.Device.Id, column, points, new(Number(p, "rate_k_per_min", 5), PhysicalUnit.KelvinPerMinute), Boolean(p, "wait_stable_first_point", true), new(Number(p, "tolerance_k", .1), PhysicalUnit.Kelvin), Integer(p, "stable_count", 3), Duration(p, "stable_timeout_s", 3600), Duration(p, p.ContainsKey("step_timeout_s") ? "step_timeout_s" : "timeout_s", 3600), TimeSpan.FromMilliseconds(Integer(p, "poll_interval_ms", 500)), sampling, Duration(p, "interval_s", 30), body) : new HardwareTemperatureScanStep(new(path), axis.Device.Id, column, points, new(Number(p, "rate_k_per_min", 5), PhysicalUnit.KelvinPerMinute), Boolean(p, "wait_stable_first_point", true), new(Number(p, "tolerance_k", .1), PhysicalUnit.Kelvin), Integer(p, "stable_count", 3), Duration(p, "stable_timeout_s", 3600), Duration(p, p.ContainsKey("step_timeout_s") ? "step_timeout_s" : "timeout_s", 3600), TimeSpan.FromMilliseconds(Integer(p, "poll_interval_ms", 500)), sampling, Duration(p, "interval_s", 30), body);
    }
}

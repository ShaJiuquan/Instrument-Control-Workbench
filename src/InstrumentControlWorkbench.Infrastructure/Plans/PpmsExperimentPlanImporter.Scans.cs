using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep Scan(string name, JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        if (name == "measure_polarization" || name == "scan_voltage" && p["voltage_mode"] is not null && IsPolarizationMode(Text(p, "voltage_mode"))) return PolarizationScan(p, node, path, c, depth);
        if (name is "scan_number" or "scan_time") return IterationScan(name, p, node, path, c, depth);
        if (name is "scan_voltage" or "scan_current" or "scan_gate")
        {
            var voltage = name != "scan_current";
            var gate = name == "scan_gate";
            if (voltage) p = VoltageScanParameters(p);
            Fields(p, "instrument", "points", "start", "end", "step", "points_mode", "channel", "settle_s", voltage ? "compliance_a" : "compliance_v", voltage ? "source_range_v" : "source_range_a", voltage ? "voltage_mode" : "current_mode");
            Choice(p, voltage ? "voltage_mode" : "current_mode", "source", "source", "bias");
            var function = voltage ? SourceFunction.Voltage : SourceFunction.Current;
            var smu = voltage ? Source(p, c, function) : null;
            var current = voltage ? null : CurrentSource(p, c, Text(p, "current_mode", "source").Trim().Equals("bias", StringComparison.OrdinalIgnoreCase) ? "k2450" : "k6221");
            var sourceDevice = smu?.Device ?? current!.Device;
            var range = voltage ? SourceRange(p, function) : SourceRange(p, current!);
            var parameter = voltage ? Keithley2450Parameters.SourceVoltage : current!.Current;
            var points = Points(p["points"] is JsonObject or JsonArray ? p["points"]! : p, parameter.Unit, c, 0, p["points"] is not (JsonObject or JsonArray));
            if (voltage && points.Values.Any(q => Math.Abs(q.Value) > 200)) throw new FormatException("PPMS voltage scan points must lie in -200..200 V.");
            foreach (var point in points.Values)
                if (voltage) { if (range is { } fixedRange) Keithley2450Limits.Validate(new SourceSetpoint(function, point.Value, Number(p, "compliance_a", 1e-4), fixedRange)); }
                else _ = new CurrentExcitationConfiguration(current!.Configuration.Model, point, new(Number(p, "compliance_v", 10), PhysicalUnit.Volt), range);
            List<ExperimentStep> sourceChildren;
            if (c.SourceScans.Any(scope => scope.Device == sourceDevice && (gate || scope.Gate)))
                throw new NotSupportedException("Nested gate/source scans of the same source cannot restore the enclosing route and level.");
            c.SourceScans.Push((sourceDevice, Text(p, "channel", gate || !voltage ? "ch1" : "").Trim(), new ParameterId(path + ".axis"), gate));
            try { sourceChildren = ScanChildren(node, path, c, depth); }
            finally { c.SourceScans.Pop(); }
            var route = gate ? GateRoute(p, path, c, smu!) : ChannelRoute(p, path, c, sourceDevice, voltage ? "" : "ch1");
            var body = new SequenceStep(new(path + ".body"), [.. ScanAxisVariables(name, new(path + ".axis"), path), new DelayStep(new(path + ".settle"), Duration(p, "settle_s", gate ? 0 : 0.1)), .. sourceChildren]);
            var steps = new List<ExperimentStep> { new SourceOutputStep(new(path + ".off"), sourceDevice, false) };
            if (route is not null) steps.Add(route);
            steps.Add(Write(path + ".zero", sourceDevice, parameter, 0));
            steps.Add(Write(path + ".compliance", sourceDevice, voltage ? Keithley2450Parameters.CurrentCompliance : current!.Compliance, Number(p, voltage ? "compliance_a" : "compliance_v", voltage ? 1e-4 : 10)));
            steps.Add(voltage ? SourceRangeStep(path, smu!, range) : SourceRangeStep(path, current!, range));
            steps.Add(Write(path + ".initial", sourceDevice, parameter, points.Values[0].Value));
            steps.Add(new SourceOutputStep(new(path + ".on"), sourceDevice, true));
            steps.Add(new ScanStep(new(path + ".scan"), new(sourceDevice, parameter.Id), new(path + ".axis"), points, TimeSpan.Zero, body));
            return new SequenceStep(new(path), steps);
        }
        if (name is not ("scan_temperature" or "scan_field" or "scan_angle")) throw new NotSupportedException($"Scan '{name}' is not migrated.");
        var role = name[5..]; var axis = Axis(role, c); var (_, rateKey, toleranceKey, tolerance, stable, timeout) = AxisDefaults(role);
        Fields(p, ["points", "start", "end", "step", "points_mode", "scan_mode", "sample_mode", "wait_mode", "settle_s", "timeout_s", "stable_duration_s", toleranceKey, .. (role == "field" ? new[] { "mode" } : role == "temperature" ? new[] { rateKey, "enable_control" } : new[] { rateKey })]);
        Choice(p, "scan_mode", "step", "step"); Choice(p, "sample_mode", "step", "step"); Choice(p, "wait_mode", "stable", "stable", "reached");
        if (role == "field") Choice(p, "mode", "driven", "driven");
        var values = Points(p["points"] is JsonObject or JsonArray ? p["points"]! : p, axis.Target.Unit, c, 0, p["points"] is not (JsonObject or JsonArray));
        var column = new ParameterId(path + ".axis");
        var wait = Stability(path + ".stable", axis, new VariableValue(column), p, toleranceKey, role == "field" ? 0.0005 : tolerance, stable, timeout);
        var settle = new DelayStep(new(path + ".settle"), Duration(p, "settle_s", 0));
        var scanChildren = ScanChildren(node, path, c, depth);
        var children = new SequenceStep(new(path + ".body"), [.. ScanAxisVariables(name, column, path), .. (role == "temperature" ? new ExperimentStep[] { settle, wait } : new ExperimentStep[] { wait, settle }), .. scanChildren]);
        if (role == "temperature")
        {
            if (!Boolean(p, "enable_control", true)) throw new NotSupportedException("Temperature scans with disabled control are not migrated.");
            foreach (var value in values.Values) { TemperatureRampPath.ValidateTemperature(value); axis.Target.ValidateWrite(value); }
            var rate = new Quantity(Number(p, rateKey, 5), PhysicalUnit.KelvinPerMinute);
            var ramp = new SoftwareTemperatureRampStep(new(path + ".ramp"), axis.Device.Id, new VariableValue(column), rate);
            return new ForEachStep(new(path + ".scan"), column, values,
                new SequenceStep(new(path + ".point"), TemperatureOverrides(path, axis, p) is { } settings ? [ramp, settings, children] : [ramp, children]));
        }
        var scan = new ScanStep(new(path + ".scan"), new(axis.Device.Id, axis.Target.Id), column, values, TimeSpan.Zero, children);
        if (role == "field") return scan;
        return new SequenceStep(new(path), [Write(path + ".rate", axis.Device.Id, axis.Rate, Number(p, rateKey, 5)), scan]);
    }
    private static ScanValues Points(JsonNode value, PhysicalUnit unit, ImportContext c, int depth, bool flat = false)
    {
        if (depth > 128) throw new FormatException("Point specification exceeds depth limit.");
        if (value is JsonArray array)
        {
            Reserve(array.Count, c);
            return new(array.Select(item => item is JsonValue v && v.TryGetValue<double>(out var n) ? new Quantity(n, unit) : throw new FormatException("Point lists require finite numbers.")));
        }
        var p = Object(value);
        if (p.ContainsKey("segments"))
        {
            Fields(p, "mode", "segments");
            if (!Text(p, "mode", "segments").Trim().Equals("segments", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Segments require segments point mode.");
            var segments = p["segments"] as JsonArray ?? throw new FormatException("segments must be an array.");
            var result = new List<Quantity>();
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = Points(segments[i] ?? throw new FormatException("Null segment."), unit, c, depth + 1);
                // PPMS drops the first point of every segment after the first,
                // even if it differs from the preceding endpoint.
                result.AddRange(i == 0 ? segment.Values : segment.Values.Skip(1));
            }
            return new(result);
        }
        var spec = !flat;
        if (spec) Fields(p, "mode", "start", "end", "points", "step", "values");
        var mode = Text(p, spec ? "mode" : "points_mode", spec ? "linear" : p.ContainsKey("step") ? "step" : "linear").Trim().ToLowerInvariant();
        if (mode == "list") return Points(p["values"] ?? throw new FormatException("list requires a values array."), unit, c, depth + 1);
        var start = Number(p, "start"); var end = Number(p, "end");
        if (mode == "step")
        {
            var step = Math.Abs(Number(p, "step"));
            if (step < 1e-18) throw new FormatException("step magnitude must be at least 1e-18.");
            if (Math.Abs(end - start) < 1e-18) { Reserve(1, c); return new([new(start, unit)]); }
            var n = Math.Floor(Math.Abs(end - start) / step + 1e-12);
            if (!double.IsFinite(n) || n > 999_998) throw new FormatException("Too many step points.");
            Reserve((int)n + 2, c); var direction = Math.Sign(end - start);
            var points = Enumerable.Range(0, (int)n + 1).Select(i => new Quantity(start + i * step * direction, unit)).ToList();
            var last = points[^1].Value;
            if ((direction > 0 && last < end - 1e-12) || (direction < 0 && last > end + 1e-12)) points.Add(new(end, unit));
            else if (Math.Abs(last - end) <= 1e-12) points[^1] = new(end, unit);
            return new(points);
        }
        if (mode is not ("linear" or "log" or "inv_t" or "inv_t2")) throw new NotSupportedException($"Point mode '{mode}' is not migrated.");
        var count = Integer(p, "points"); Reserve(count, c);
        if (mode == "log" && (start <= 0 || end <= 0)) throw new FormatException("Log endpoints must be positive.");
        if (mode is "inv_t" or "inv_t2" && (start == 0 || end == 0)) throw new FormatException("Inverse endpoints must be non-zero.");
        if (count == 1 && mode is "linear" or "log") return new([new(start, unit)]);
        if (mode == "linear") { var step = (end - start) / (count - 1); return new(Enumerable.Range(0, count).Select(i => new Quantity(start + step * i, unit))); }
        if (mode == "log") { var first = Math.Log(start); var step = (Math.Log(end) - first) / (count - 1); return new(Enumerable.Range(0, count).Select(i => new Quantity(Math.Exp(first + step * i), unit))); }
        var power = mode == "inv_t2" ? 2 : 1;
        var initial = 1 / Math.Pow(start, power); var delta = count == 1 ? 0 : (1 / Math.Pow(end, power) - initial) / (count - 1);
        return new(Enumerable.Range(0, count).Select(i => new Quantity(1 / Math.Pow(initial + delta * i, 1.0 / power), unit)));
    }
    private static void Reserve(int count, ImportContext c)
    {
        if (count < 1 || count > 1_000_000 || (c.Values += count) > 1_000_000) throw new FormatException("Imported points exceed the aggregate 1,000,000-value budget.");
    }
}

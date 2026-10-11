using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep Command(string name, JsonObject p, string path, ImportContext c)
    {
        switch (name)
        {
            case "k2182.set_nplc":
            case "k2182.set_range":
            case "k2182.set_filter":
                return MeterCommand(name, p, path, c);
            case "k6221.set_source_delay":
            case "k6221.set_current":
            case "k6221.set_compliance":
            case "k6221.set_output":
            case "k6221.set_range":
            case "k6221.set_source_range":
            case "k6221.set_auto_range":
            case "k6221.set_source_auto_range":
                return CurrentSourceCommand(name, p, path, c);
            case "wait":
                Fields(p, "seconds", "wait_kind"); Choice(p, "wait_kind", "wait", "wait");
                return new DelayStep(new(path), Duration(p, "seconds", 5));
            case "set_temperature":
            case "set_field":
            case "set_angle":
                return SetAxis(name[4..], p, path, c);
            case "set_voltage":
            case "set_current":
                return SetSource(name == "set_voltage" ? SourceFunction.Voltage : SourceFunction.Current, p, path, c);
            case "k2450.set_output":
            case "k2450.set_output_enabled":
                Fields(p, "enabled"); if (!p.ContainsKey("enabled")) throw new FormatException("enabled is required.");
                var enabled = Boolean(p, "enabled", false);
                if (!c.Bindings.Instruments.TryGetValue("k2450", out var outputDevice)) throw new NotSupportedException("k2450 needs an explicit import binding.");
                // An off-only command must not apply an unused preparation and
                // change the connected source's function/level before stopping it.
                if (enabled) _ = Source(p, c, null);
                return new SourceOutputStep(new(path), outputDevice, enabled);
            case "k2450.set_source_level":
                Fields(p, "function", "level", "instrument"); var function = Function(p);
                var level = Source(p, c, function);
                return Write(path, level.Device, function == SourceFunction.Voltage ? Keithley2450Parameters.SourceVoltage : Keithley2450Parameters.SourceCurrent, Number(p, "level"));
            case "k2450.set_compliance":
                Fields(p, "function", "compliance", "instrument"); var configured = Source(p, c, Function(p));
                return Write(path, configured.Device, configured.Function == SourceFunction.Voltage ? Keithley2450Parameters.CurrentCompliance : Keithley2450Parameters.VoltageCompliance, Number(p, "compliance"));
            case "k2450.set_nplc":
                Fields(p, "function", "nplc", "instrument"); var meter = Source(p, c, null);
                if (Function(p) != (meter.Sense.Function == SenseFunction.Voltage ? SourceFunction.Voltage : SourceFunction.Current))
                    throw new NotSupportedException("NPLC sensing function differs from the bound preparation.");
                return Write(path, meter.Device, Keithley2450Parameters.Nplc, Number(p, "nplc"));
            default: throw new NotSupportedException($"Command '{name}' is not migrated.");
        }
    }
    private static SourceFunction Function(JsonObject p) => Text(p, "function").Trim().ToLowerInvariant() switch
    {
        "voltage" or "volt" or "v" => SourceFunction.Voltage,
        "current" or "curr" or "i" or "a" => SourceFunction.Current,
        _ => throw new FormatException("Unknown source/sense function."),
    };
    private static SourcePreparation Source(JsonObject p, ImportContext c, SourceFunction? function, string defaultName = "k2450")
    {
        var name = Text(p, "instrument", defaultName).Trim();
        if (!c.Bindings.Instruments.TryGetValue(name, out var device)) throw new NotSupportedException($"Instrument '{name}' needs an explicit import binding.");
        var preparation = c.Bindings.Preparations.SingleOrDefault(s => s.Device == device) ?? throw new NotSupportedException($"{device} needs an explicit source preparation.");
        if (function is { } expected && preparation.Function != expected) throw new NotSupportedException("Changing source function inside an imported tree is not migrated.");
        c.Sources.Add(device); return preparation;
    }
    private static AxisControllerDefinition Axis(string role, ImportContext c)
    {
        if (!c.Bindings.Axes.TryGetValue(role, out var axis)) throw new NotSupportedException($"Axis '{role}' needs an explicit import binding.");
        var expected = role switch { "temperature" => PhysicalUnit.Kelvin, "field" => PhysicalUnit.Tesla, "angle" => PhysicalUnit.Degree, _ => throw new NotSupportedException($"Unknown axis {role}.") };
        if (axis.Target.Unit != expected) throw new ArgumentException($"Bound {role} axis uses incorrect units.");
        return axis;
    }
    private static SetParameterStep Write(string id, InstrumentId device, ParameterDescriptor parameter, double value)
        => new(new(id), new(device, parameter.Id), new ConstantValue(new(value, parameter.Unit)));
    private static ExperimentStep SetSource(SourceFunction function, JsonObject p, string path, ImportContext c)
    {
        var voltage = function == SourceFunction.Voltage;
        Fields(p, "instrument", "channel", voltage ? "target_v" : "target_a", voltage ? "compliance_a" : "compliance_v",
            voltage ? "source_range_v" : "source_range_a", voltage ? "voltage_mode" : "current_mode");
        Choice(p, voltage ? "voltage_mode" : "current_mode", "source", "source");
        var smu = voltage ? Source(p, c, function) : null;
        var current = voltage ? null : CurrentSource(p, c);
        var sourceDevice = smu?.Device ?? current!.Device;
        var range = voltage ? SourceRange(p, function) : SourceRange(p, current!);
        var target = Number(p, voltage ? "target_v" : "target_a");
        if (voltage && Math.Abs(target) > 200) throw new FormatException("PPMS voltage targets must lie in -200..200 V.");
        if (voltage) { if (range is { } fixedRange) Keithley2450Limits.Validate(new SourceSetpoint(function, target, Number(p, "compliance_a", 1e-4), fixedRange)); }
        else _ = new CurrentExcitationConfiguration(current!.Configuration.Model, new(target, PhysicalUnit.Ampere), new(Number(p, "compliance_v", 1), PhysicalUnit.Volt), range);
        var steps = new List<ExperimentStep> { new SourceOutputStep(new(path + ".off"), sourceDevice, false) };
        if (ChannelRoute(p, path, c, sourceDevice, "ch1") is { } route) steps.Add(route);
        // Park at zero before changing compliance so a previous high level cannot
        // invalidate an otherwise safe new level/compliance pair.
        steps.Add(Write(path + ".zero", sourceDevice, voltage ? Keithley2450Parameters.SourceVoltage : current!.Current, 0));
        steps.Add(Write(path + ".compliance", sourceDevice, voltage ? Keithley2450Parameters.CurrentCompliance : current!.Compliance, Number(p, voltage ? "compliance_a" : "compliance_v", voltage ? 1e-4 : 1)));
        steps.Add(voltage ? SourceRangeStep(path, smu!, range) : SourceRangeStep(path, current!, range));
        steps.Add(Write(path + ".level", sourceDevice, voltage ? Keithley2450Parameters.SourceVoltage : current!.Current, target));
        steps.Add(new SourceOutputStep(new(path + ".on"), sourceDevice, true));
        return new SequenceStep(new(path), steps);
    }
    private static RouteMatrixStep? ChannelRoute(JsonObject p, string path, ImportContext c, InstrumentId source, string fallback, string? measurementMode = null)
    {
        var channel = Text(p, "channel", fallback).Trim();
        if (channel.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (!c.Bindings.Channels.TryGetValue(channel, out var binding) && c.Bindings.ChannelProfiles is { } profiles)
        {
            var smu = c.Bindings.Preparations.SingleOrDefault(p => p.Device == source);
            var pure = c.Bindings.CurrentSourcePreparations.Any(p => p.Device == source);
            if (smu is null && !pure) throw new NotSupportedException("Catalog routing needs an explicit source preparation.");
            binding = profiles.Resolve(channel, profiles.VoltageMeter is not null ? SensingMode.TwoWire : smu?.Sense.Mode ?? SensingMode.TwoWire,
                measurementMode ?? (pure || profiles.VoltageMeter is not null ? "curr_dc" : null)); c.UsesChannelProfiles = true;
        }
        if (binding is null || binding.Source != source)
            throw new NotSupportedException($"Channel '{channel}' needs an explicit route for {source}; use channel='none' only for externally wired experiments.");
        return new(new(path + ".route"), binding.Route.Device, binding.Route.Closed, binding.Route.Settle);
    }
    private static ExperimentStep SetAxis(string role, JsonObject p, string path, ImportContext c)
    {
        var axis = Axis(role, c); var (targetKey, rateKey, toleranceKey, tolerance, stable, timeout) = AxisDefaults(role);
        Fields(p, [targetKey, "wait_mode", toleranceKey, "stable_duration_s", "settle_s", "timeout_s", .. (role == "field" ? new[] { "mode" } : role == "temperature" ? new[] { rateKey, "ramp_strategy", "enable_control" } : new[] { rateKey })]);
        if (role == "field") Choice(p, "mode", "driven", "driven");
        if (role == "temperature") Choice(p, "ramp_strategy", "software", "software", "hardware", "auto");
        var target = Number(p, targetKey); var steps = new List<ExperimentStep>();
        if (role == "temperature")
        {
            if (!Boolean(p, "enable_control", true)) throw new NotSupportedException("Temperature sets with disabled control are not migrated.");
            TemperatureRampPath.ValidateTemperature(new(target, PhysicalUnit.Kelvin));
            var requestedRate = Number(p, rateKey, 0);
            Quantity? rate = requestedRate > 0 ? new(requestedRate, PhysicalUnit.KelvinPerMinute) : null;
            if (rate is { } positiveRate) TemperatureRampPath.ValidateRate(positiveRate);
            var strategy = Text(p, "ramp_strategy", "software").Trim().ToLowerInvariant();
            if (strategy == "hardware" && rate is { } hardwareRate)
            {
                steps.Add(Write(path + ".rate", axis.Device.Id, axis.Rate, hardwareRate.Value));
                steps.Add(Write(path + ".target", axis.Device.Id, axis.Target, target));
            }
            else steps.Add(new SoftwareTemperatureRampStep(new(path + ".target"), axis.Device.Id, new ConstantValue(new(target, PhysicalUnit.Kelvin)), rate));
        }
        else
        {
            if (role != "field") steps.Add(Write(path + ".rate", axis.Device.Id, axis.Rate, Number(p, rateKey, 5)));
            steps.Add(Write(path + ".target", axis.Device.Id, axis.Target, target));
        }
        Choice(p, "wait_mode", "stable", "stable", "reached", "none");
        if (Text(p, "wait_mode", "stable").Trim().ToLowerInvariant() != "none")
            steps.Add(Stability(path + ".stable", axis, new ConstantValue(new(target, axis.Target.Unit)), p, toleranceKey, tolerance,
                Text(p, "wait_mode", "stable").Trim().Equals("reached", StringComparison.OrdinalIgnoreCase) ? 0 : stable, timeout));
        steps.Add(new DelayStep(new(path + ".settle"), Duration(p, "settle_s", 0)));
        return new SequenceStep(new(path), steps);
    }
    private static (string Target, string Rate, string Tolerance, double ToleranceValue, double Stable, double Timeout) AxisDefaults(string role) => role switch
    {
        "temperature" => ("target_k", "rate_k_per_min", "tolerance_k", 0.1, 5, 3600),
        "field" => ("target_t", "unused_field_rate", "tolerance_t", 0.005, 0, 7200),
        "angle" => ("target_deg", "speed_deg_per_s", "tolerance_deg", 0.1, 0, 30),
        _ => throw new NotSupportedException($"Unknown axis {role}."),
    };
    private static WaitStableStep Stability(string path, AxisControllerDefinition axis, PlanValue target, JsonObject p, string toleranceKey, double tolerance, double stable, double timeout)
        => new(new(path), new(axis.Device.Id, axis.Readback.Id), target, new(Number(p, toleranceKey, tolerance), axis.Target.Unit),
            Text(p, "wait_mode", "stable").Trim().Equals("reached", StringComparison.OrdinalIgnoreCase) ? TimeSpan.Zero : Duration(p, "stable_duration_s", stable), TimeSpan.FromMilliseconds(50), Duration(p, "timeout_s", timeout));
    private static ExperimentStep Measurement(string name, JsonObject p, string path, ImportContext c)
    {
        if (name is "meas_r" or "meas_r_sample" or "meas_r_2w" or "meas_r_4w" or "meas_r_hallbar" or "meas_r_vdp")
            return Resistance(name, p, path, c);
        if (name is "measure_temp" or "measure_field" or "measure_angle")
        {
            Fields(p, "meas_type", "name"); var axis = Axis(name == "measure_temp" ? "temperature" : name[8..], c);
            return new ReadStep(new(path), [new(new(path + ".value"), new(axis.Device.Id, axis.Readback.Id))], MeasurementEnvironment(path, c));
        }
        if (name is not ("measure_voltage" or "measure_current")) throw new NotSupportedException($"Measurement '{name}' is not migrated.");
        Fields(p, "meas_type", "name", "instrument", "channel", "sensing_mode", "nplc", "meter_range_v", "meter_range_a", "sense_channel", "filter_enabled", "filter_count", "filter_window_percent");
        var defaultInstrument = name == "measure_voltage" ? "2182" : "k2450";
        if (name == "measure_voltage" && c.Bindings.Instruments.TryGetValue("k2182", out var defaultMeter) && c.Bindings.StandaloneVoltageMeters.Contains(defaultMeter))
            defaultInstrument = "k2182";
        var instrument = Text(p, "instrument", defaultInstrument).Trim();
        if (c.Bindings.Instruments.TryGetValue(instrument, out var standalone) && c.Bindings.StandaloneVoltageMeters.Contains(standalone))
        {
            if (name != "measure_voltage") throw new NotSupportedException("K2182 has no current measurement capability.");
            return StandaloneVoltage(p, path, standalone, c);
        }
        var senseFunction = name == "measure_voltage" ? SenseFunction.Voltage : SenseFunction.Current;
        var selector = RangeSelector(p, name == "measure_voltage" ? "meter_range_v" : "meter_range_a", senseFunction == SenseFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere);
        Quantity? senseRange = selector is { } chosen ? Keithley2450SenseRanges.Resolve(senseFunction, chosen) : null;
        if (p[name == "measure_voltage" ? "meter_range_a" : "meter_range_v"] is not null)
            throw new NotSupportedException("The range field must match the requested measurement function.");
        var nplc = Number(p, "nplc", 1);
        var filterEnabled = Boolean(p, "filter_enabled", false); var filterCount = Number(p, "filter_count", 10);
        if (filterCount is < 2 or > 100 || filterCount != Math.Truncate(filterCount)) throw new ArgumentException("PPMS filter count must be an integer from 2 to 100.");
        if (filterEnabled && nplc * filterCount > (name == "measure_voltage" ? 200 : 500)) throw new ArgumentException("NPLC times filter count exceeds the PPMS measurement budget.");
        var window = Number(p, "filter_window_percent", 0.1);
        if (window is < 0 or > 10) throw new ArgumentException("PPMS filter window percent must be from 0 to 10.");
        if (name == "measure_voltage") c.Metadata[path + ".k2450_unused_filter_window_percent"] = new(window, PhysicalUnit.Dimensionless);
        else if (p["filter_window_percent"] is not null) throw new NotSupportedException("PPMS current filtering has no window percentage.");
        if (Number(p, "sense_channel", 1) != 1) throw new NotSupportedException("Secondary meter inputs are not migrated.");
        var source = Source(p, c, null, name == "measure_voltage" ? "2182" : "k2450");
        var mode = Text(p, "sensing_mode", "2wire").Trim().ToLowerInvariant();
        if (mode != (source.Sense.Mode == SensingMode.FourWire ? "4wire" : "2wire"))
            throw new NotSupportedException("Meter settings differ from the explicit source preparation.");
        if (!Text(p, "channel", "").Trim().Equals("", StringComparison.Ordinal) && !Text(p, "channel").Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Routed standalone meter reads are not migrated; source scan routes may be explicitly bound.");
        if (senseRange is not null && source.Sense.Function != senseFunction)
            throw new NotSupportedException("Manual meter range requires the requested sensing function in the frozen preparation.");
        var rangeParameter = senseRange is null ? Keithley2450Parameters.EnableSenseAutoRange : Keithley2450Parameters.ManualSenseRange(senseFunction);
        var settings = new List<ExperimentStep> { Write(path + ".sense_function", source.Device, Keithley2450Parameters.ActiveSenseFunction, (int)senseFunction), Write(path + ".filter_off", source.Device, Keithley2450Parameters.AveragingEnabled, 0) };
        if (filterEnabled)
        {
            settings.Add(Write(path + ".filter_count", source.Device, Keithley2450Parameters.AveragingCount, filterCount));
            settings.Add(Write(path + ".filter_mode", source.Device, Keithley2450Parameters.AveragingMode, (int)AveragingMode.Repeat));
            settings.Add(Write(path + ".filter_on", source.Device, Keithley2450Parameters.AveragingEnabled, 1));
        }
        return new SequenceStep(new(path), [.. settings, Write(path + ".sense_range", source.Device, rangeParameter, senseRange?.Value ?? 1), Write(path + ".nplc", source.Device, Keithley2450Parameters.Nplc, Number(p, "nplc", 1)), new ReadStep(new(path + ".read"), [new(new(path + ".value"), new(source.Device, name == "measure_voltage" ? Keithley2450Parameters.MeasuredVoltage.Id : Keithley2450Parameters.MeasuredCurrent.Id))], MeasurementEnvironment(path, c))]);
    }
}

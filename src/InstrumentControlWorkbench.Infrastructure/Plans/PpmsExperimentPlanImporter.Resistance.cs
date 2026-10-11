using System.Globalization;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep Resistance(string name, JsonObject original, string path, ImportContext c)
    {
        var p = (JsonObject)original.DeepClone();
        Alias(p, "sample_id", "sample"); Alias(p, "mode", "meas_mode");
        Alias(p, "voltage_v", "target_v", "bias_v", "voltage"); Alias(p, "compliance_a", "current_limit_a", "compliance");
        Alias(p, "source_range_v", "range_v", "voltage_range_v"); Alias(p, "meter_range_a", "sense_range_a", "current_range_a");
        Alias(p, "nplc", "sense_nplc"); Alias(p, "sensing_mode", "sense_mode");
        Alias(p, "source_managed", "source_managed_externally"); Alias(p, "settle_s", "measure_settle_s");
        Alias(p, "meter_instrument", "sense_instrument"); Alias(p, "sense_channel", "k2182_channel");
        Fields(p, "meas_type", "name", "mode", "sample_id", "voltage_v", "compliance_a", "source_range_v", "meter_range_a", "nplc", "sensing_mode", "source_managed", "settle_s", "channel",
            "source_instrument", "meter_instrument", "reference_instrument", "lock_in_instrument", "voltage_source_instrument", "current_meter_instrument", "sense_channel", "record_temperature", "record_field", "record_angle");
        Choice(p, "mode", "curr_dc", "volt_dc");
        foreach (var optional in new[] { "source_range_v", "meter_range_a", "sensing_mode" })
            if (p[optional] is null) p.Remove(optional);
        var range = SourceRange(p, SourceFunction.Voltage);
        var senseSelector = RangeSelector(p, "meter_range_a", PhysicalUnit.Ampere);
        Quantity? senseRange = senseSelector is { } selected ? Keithley2450SenseRanges.Resolve(SenseFunction.Current, selected) : null;
        foreach (var environment in new[] { "record_temperature", "record_field", "record_angle" })
            _ = Boolean(p, environment, false); // Current Rust captures all three; these are legacy authoring flags.
        if (ResistanceNumber(p, "sense_channel", 1) != 1) throw new NotSupportedException("Only the primary integrated meter input is migrated.");
        var source = Source(new JsonObject { ["instrument"] = "k2450" }, c, SourceFunction.Voltage);
        foreach (var role in new[] { "source_instrument", "meter_instrument", "reference_instrument", "lock_in_instrument", "voltage_source_instrument", "current_meter_instrument" })
            if (p[role] is not null && Text(p, role).Trim().Length != 0 && (!c.Bindings.Instruments.TryGetValue(Text(p, role).Trim(), out var device) || device != source.Device))
                throw new NotSupportedException($"{role} must explicitly bind the integrated volt_dc source/meter instance.");
        if (source.Sense.Function != SenseFunction.Current) throw new NotSupportedException("volt_dc requires prepared current sensing.");
        var voltage = ResistanceNumber(p, "voltage_v", 0.1); var compliance = ResistanceNumber(p, "compliance_a", 1e-4); var nplc = ResistanceNumber(p, "nplc", 1);
        if (Math.Abs(voltage) > 200 || compliance is < 1e-9 or > 1.05 || nplc is < 0.01 or > 10) throw new FormatException("volt_dc voltage, compliance or NPLC is outside the PPMS profile.");
        Keithley2450Limits.Validate(new SourceSetpoint(SourceFunction.Voltage, voltage, compliance));
        p["settle_s"] = ResistanceNumber(p, "settle_s", 0.1);
        var settle = Duration(p, "settle_s", 0.1); var sampleMeasurement = name != "meas_r";
        var scope = c.SourceScans.FirstOrDefault(s => s.Device == source.Device);
        var managed = Boolean(p, "source_managed", false) || scope.Device == source.Device;
        if (!managed) Keithley2450Limits.ValidateConfiguration(new SourceSetpoint(SourceFunction.Voltage, voltage, compliance, range), source.Sense with { AutoRange = senseRange is null, ManualRange = senseRange });
        SampleProfile? sample = null;
        if (sampleMeasurement)
        {
            if (managed) throw new NotSupportedException("PPMS sample measurements must own source cleanup; enclosing scans of the same source are not accepted.");
            sample = (c.Bindings.Samples ?? throw new NotSupportedException("Sample measurements need frozen sample/wiring import bindings.")).ResolveSample(Text(p, "sample_id"));
            var expected = name switch { "meas_r_2w" => SampleTopology.TwoWire, "meas_r_4w" => SampleTopology.FourWire, "meas_r_hallbar" => SampleTopology.HallBar, "meas_r_vdp" => SampleTopology.VanDerPauw, _ => sample.Topology };
            if (sample.Topology != expected) throw new FormatException("Requested sample primitive differs from the configured topology.");
            if (p.ContainsKey("channel") && Text(p, "channel").Trim().Length != 0) throw new NotSupportedException("Sample electrode links determine routing; a channel override is not accepted.");
            c.UsesSamples = true;
        }
        else if (p.ContainsKey("sample_id")) throw new FormatException("Channel resistance cannot silently use a sample identifier.");
        var expectedMode = sample is not null && sample.Topology != SampleTopology.TwoWire ? SensingMode.FourWire : SensingMode.TwoWire;
        if (p.ContainsKey("sensing_mode")) expectedMode = WireMode(p["sensing_mode"]);
        if (sample is not null && (expectedMode == SensingMode.FourWire) != (sample.Topology != SampleTopology.TwoWire))
            throw new FormatException("Sensing mode must match sample topology.");
        if (source.Sense.Mode != expectedMode) throw new NotSupportedException("Requested sensing mode differs from the frozen source preparation.");
        var steps = new List<ExperimentStep>(); RouteMatrixStep? route = null;
        if (!managed) steps.Add(new SourceOutputStep(new(path + ".off"), source.Device, false));
        if (!sampleMeasurement)
        {
            var routingParams = (JsonObject)p.DeepClone();
            if ((!routingParams.ContainsKey("channel") || Text(routingParams, "channel").Trim().Length == 0) && scope.Device == source.Device)
                routingParams["channel"] = scope.Channel;
            route = ChannelRoute(routingParams, path, c, source.Device, "ch1", "volt_dc");
            if (route is not null) steps.Add(managed ? new VerifyMatrixRouteStep(route.Id, route.Device, route.Closed) : route);
        }
        if (!managed)
        {
            steps.Add(Write(path + ".sense_auto", source.Device, Keithley2450Parameters.EnableSenseAutoRange, 1));
            steps.Add(Write(path + ".zero", source.Device, Keithley2450Parameters.SourceVoltage, 0));
            steps.Add(Write(path + ".compliance", source.Device, Keithley2450Parameters.CurrentCompliance, compliance));
            steps.Add(SourceRangeStep(path, source, range));
            steps.Add(Write(path + ".level", source.Device, Keithley2450Parameters.SourceVoltage, voltage));
        }
        if (senseRange is { } span) steps.Add(Write(path + ".sense_range", source.Device, Keithley2450Parameters.ManualSenseCurrentRange, span.Value));
        else if (managed) steps.Add(Write(path + ".sense_range", source.Device, Keithley2450Parameters.EnableSenseAutoRange, 1));
        steps.Add(Write(path + ".nplc", source.Device, Keithley2450Parameters.Nplc, nplc));
        var minimum = new Quantity(2.220446049250313e-16, PhysicalUnit.Ampere); // Rust f64::EPSILON, not .NET double.Epsilon.
        var environmentReads = MeasurementEnvironment(path, c);
        if (sample is not null)
        {
            var binding = c.Bindings.Samples!;
            if (!binding.ModeWirings.TryGetValue("volt_dc", out var wiring)) throw new NotSupportedException("volt_dc needs an explicit sample mode-to-wiring binding.");
            var selections = sample.Topology switch
            {
                SampleTopology.TwoWire => new[] { SampleRouteSelection.TwoWireResistance },
                SampleTopology.FourWire => new[] { SampleRouteSelection.FourWireResistance },
                _ => SampleResistanceAnalysis.Selections(sample.Topology).ToArray(),
            };
            var reads = selections.Select((selection, index) => new ReadSampleResistanceStep(
                new(new(path + ".read." + index), source.Device, SourceFunction.Voltage, new(path + ".V_V"), new(path + ".I_A"), new(path + ".R_ohms"), minimum, environmentReads),
                new(sample.Id, wiring, selection), binding.RouteSettle, acquisitionSettle: settle)).ToArray();
            if (sample.Topology is SampleTopology.HallBar or SampleTopology.VanDerPauw)
                steps.Add(new SampleAnalysisStep(new(path + ".analysis"), reads,
                    SampleResistanceAnalysis.Kinds(sample.Topology).Select(kind => new SampleDerivedColumn(kind, new(path + "." + kind)))));
            else steps.AddRange(reads);
        }
        else
        {
            if (!managed) steps.Add(new SourceOutputStep(new(path + ".on"), source.Device, true));
            steps.Add(new DelayStep(new(path + ".settle"), settle));
            if (route is not null) steps.Add(new VerifyMatrixRouteStep(new(path + ".verify"), route.Device, route.Closed));
            steps.Add(new ReadResistanceStep(new(path + ".read"), source.Device, SourceFunction.Voltage, new(path + ".V_V"), new(path + ".I_A"), new(path + ".R_ohms"), minimum, environmentReads));
            if (!managed) steps.Add(new SourceOutputStep(new(path + ".done"), source.Device, false));
        }
        return new SequenceStep(new(path), steps);
    }
    private static double ResistanceNumber(JsonObject p, string key, double fallback)
    {
        if (p[key] is null) return fallback;
        if (p[key] is JsonValue value && value.TryGetValue<string>(out var text))
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? number : throw new FormatException($"{key} requires a finite numeric value.");
        return Number(p, key);
    }
    private static void Alias(JsonObject p, string canonical, params string[] aliases)
    {
        var names = new[] { canonical }.Concat(aliases).Where(p.ContainsKey).ToArray();
        if (names.Length > 1) throw new FormatException($"Conflicting parameter aliases for {canonical}.");
        if (names.Length == 1 && names[0] != canonical)
        { var node = p[names[0]]?.DeepClone(); p.Remove(names[0]); p[canonical] = node; }
    }
    private static SensingMode WireMode(JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<bool>(out var four)) return four ? SensingMode.FourWire : SensingMode.TwoWire;
        if (value is JsonValue s && s.TryGetValue<string>(out var text)) return text.Trim().ToLowerInvariant() switch
        {
            "4wire" or "4_wire" or "four_wire" or "kelvin" => SensingMode.FourWire,
            "2wire" or "2_wire" or "two_wire" => SensingMode.TwoWire,
            _ => throw new FormatException("Unknown sensing mode."),
        };
        throw new FormatException("Sensing mode requires a boolean or a known wire-mode string.");
    }
}

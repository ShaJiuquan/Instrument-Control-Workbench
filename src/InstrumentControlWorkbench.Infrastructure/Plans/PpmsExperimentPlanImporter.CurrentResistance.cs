using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep CurrentResistance(string name, JsonObject p, string path, ImportContext c)
    {
        Alias(p, "sample_id", "sample"); Alias(p, "sensing_mode", "sense_mode");
        Alias(p, "mode", "meas_mode"); Alias(p, "current_a", "current", "excitation_a", "i_a");
        Alias(p, "compliance_v", "compliance"); Alias(p, "source_range_a", "range_a", "current_range_a");
        Alias(p, "meter_range_v", "sense_range_v", "voltage_range_v"); Alias(p, "nplc", "sense_nplc", "meter_nplc");
        Alias(p, "sense_channel", "k2182_channel"); Alias(p, "source_managed", "source_managed_externally");
        Alias(p, "settle_s", "measure_settle_s"); Alias(p, "meter_instrument", "sense_instrument");
        Fields(p, "meas_type", "name", "mode", "current_a", "compliance_v", "source_range_a", "meter_range_v", "nplc", "sense_channel", "source_managed", "settle_s",
            "channel", "source_instrument", "meter_instrument", "sample_id", "sensing_mode", "reference_instrument", "lock_in_instrument", "voltage_source_instrument", "current_meter_instrument", "record_temperature", "record_field", "record_angle");
        var sampleMeasurement = name != "meas_r";
        if (!sampleMeasurement && p.ContainsKey("sensing_mode")) throw new NotSupportedException("Channel curr_dc does not configure SMU sensing mode.");
        if (p["sensing_mode"] is not null) _ = WireMode(p["sensing_mode"]);
        Choice(p, "mode", "curr_dc", "curr_dc");
        var sourceName = Text(p, "source_instrument", "k6221").Trim();
        var meterName = Text(p, "meter_instrument", "k2182").Trim();
        var source = CurrentSource(new JsonObject { ["instrument"] = sourceName }, c);
        if (!c.Bindings.Instruments.TryGetValue(meterName, out var meter) || !c.Bindings.StandaloneVoltageMeters.Contains(meter) || meter == source.Device)
            throw new NotSupportedException("curr_dc requires an explicitly bound independent voltage meter.");
        foreach (var role in new[] { "reference_instrument", "voltage_source_instrument", "lock_in_instrument", "current_meter_instrument" })
            if (p[role] is not null && Text(p, role).Trim().Length != 0 && (!c.Bindings.Instruments.TryGetValue(Text(p, role).Trim(), out var boundRole) ||
                boundRole != (role is "reference_instrument" or "voltage_source_instrument" ? source.Device : meter)))
                throw new NotSupportedException($"{role} differs from the explicitly bound curr_dc source/meter.");
        var input = ResistanceNumber(p, "sense_channel", 1);
        if (input is < 1 or > 2 || input != Math.Truncate(input)) throw new FormatException("K2182 input must be an integer 1 or 2.");
        var rangeSelector = RangeSelector(p, "meter_range_v", PhysicalUnit.Volt);
        Quantity? meterRange = rangeSelector is { } selector ? Keithley2182Settings.ResolveRange((int)input, selector) : null;
        var nplc = ResistanceNumber(p, "nplc", 1); Keithley2182Parameters.Nplc.ValidateWrite(new(nplc, PhysicalUnit.Dimensionless));
        var sourceRange = SourceRange(p, source); var compliance = ResistanceNumber(p, "compliance_v", 10);
        var delay = new DelayStep(new(path + ".settle"), TimeSpan.FromSeconds(ResistanceNumber(p, "settle_s", 0.1))); var settle = delay.Duration;
        foreach (var flag in new[] { "record_temperature", "record_field", "record_angle" }) _ = Boolean(p, flag, false);
        var scope = c.SourceScans.FirstOrDefault(s => s.Device == source.Device); var managed = Boolean(p, "source_managed", false) || scope.Device == source.Device;
        var threshold = new Quantity(2.220446049250313e-16, PhysicalUnit.Ampere);
        var current = new Quantity(scope.Device == source.Device ? source.Configuration.Level : ResistanceNumber(p, "current_a", 1e-5), PhysicalUnit.Ampere);
        PlanValue expected = scope.Device == source.Device ? new VariableValue(scope.Axis) : new ConstantValue(current);
        if (!managed)
        {
            _ = new CurrentExcitationConfiguration(source.Configuration.Model, current, new(compliance, PhysicalUnit.Volt), sourceRange);
            if (source.Smu is { } smu) Keithley2450Limits.ValidateConfiguration(new(SourceFunction.Current, current.Value, compliance, sourceRange), smu.Sense with { AutoRange = true, ManualRange = null });
        }
        SampleProfile? sample = null; WiringProfileId? sampleWiring = null;
        if (sampleMeasurement)
        {
            if (managed) throw new NotSupportedException("Independent sample measurements must own source cleanup; scans of their excitation source are not accepted.");
            if (input != 1) throw new NotSupportedException("Sample wiring targets K2182 input 1.");
            if (p.ContainsKey("channel") && Text(p, "channel").Trim().Length != 0) throw new NotSupportedException("Sample electrodes determine routing; channel override is not accepted.");
            var samples = c.Bindings.Samples ?? throw new NotSupportedException("Independent samples need frozen sample/wiring bindings.");
            sample = samples.ResolveSample(Text(p, "sample_id"));
            var topology = name switch { "meas_r_2w" => SampleTopology.TwoWire, "meas_r_4w" => SampleTopology.FourWire, "meas_r_hallbar" => SampleTopology.HallBar, "meas_r_vdp" => SampleTopology.VanDerPauw, _ => sample.Topology };
            if (topology != sample.Topology) throw new FormatException("Sample primitive differs from its configured topology.");
            if (!samples.ModeWirings.TryGetValue("curr_dc", out sampleWiring)) throw new NotSupportedException("Independent sample mode requires curr_dc wiring.");
            var wiring = samples.Routing.Wirings.Single(w => w.Id == sampleWiring);
            if (wiring.SourceMeter != source.Device || wiring.VoltageMeter != meter) throw new NotSupportedException("Sample wiring differs from the selected current source and voltage meter.");
            c.UsesSamples = true;
        }
        else if (p.ContainsKey("sample_id")) throw new FormatException("Channel acquisition cannot silently use a sample identifier.");
        var channel = sampleMeasurement ? "none" : Text(p, "channel", "").Trim(); if (channel == "" && scope.Device == source.Device) channel = scope.Channel;
        if (channel == "") channel = "ch1";
        ImportChannelRoute? binding = null;
        if (!channel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!c.Bindings.Channels.TryGetValue(channel, out binding) && c.Bindings.ChannelProfiles is { } profiles)
            {
                binding = profiles.Resolve(channel, SensingMode.TwoWire, "curr_dc"); c.UsesChannelProfiles = true;
            }
            if (binding is null || binding.Source != source.Device || binding.VoltageMeter != meter)
                throw new NotSupportedException($"Channel '{channel}' needs an explicit curr_dc route binding for this source and independent meter.");
        }
        var steps = new List<ExperimentStep>(); if (!managed) steps.Add(new SourceOutputStep(new(path + ".off"), source.Device, false));
        if (binding is { } routed)
            steps.Add(managed ? new VerifyMatrixRouteStep(new(path + ".route"), routed.Route.Device, routed.Route.Closed) : new RouteMatrixStep(new(path + ".route"), routed.Route.Device, routed.Route.Closed, routed.Route.Settle));
        steps.Add(Write(path + ".input", meter, Keithley2182Parameters.Channel, input));
        steps.Add(Write(path + ".meter_range", meter, meterRange is null ? Keithley2182Parameters.EnableAutoRange : Keithley2182Parameters.ManualRange, meterRange?.Value ?? 1));
        steps.Add(Write(path + ".nplc", meter, Keithley2182Parameters.Nplc, nplc));
        if (!managed)
        {
            if (!source.Pure) steps.Add(Write(path + ".sense_auto", source.Device, Keithley2450Parameters.EnableSenseAutoRange, 1));
            steps.Add(Write(path + ".zero", source.Device, source.Current, 0));
            steps.Add(Write(path + ".compliance", source.Device, source.Compliance, compliance));
            steps.Add(SourceRangeStep(path, source, sourceRange)); steps.Add(Write(path + ".level", source.Device, source.Current, current.Value));
            if (!sampleMeasurement) { steps.Add(new SourceOutputStep(new(path + ".on"), source.Device, true)); steps.Add(delay); }
        }
        else
        {
            c.Metadata[path + ".curr_dc_managed_compliance_v_unused"] = new(compliance, PhysicalUnit.Volt);
            if (sourceRange is { } ignoredRange) c.Metadata[path + ".curr_dc_managed_source_range_a_unused"] = ignoredRange;
            c.Metadata[path + ".curr_dc_managed_settle_s_unused"] = new(settle.TotalSeconds, PhysicalUnit.Second);
        }
        if (sample is not null)
        {
            var samples = c.Bindings.Samples!;
            var selections = sample.Topology switch
            {
                SampleTopology.TwoWire => new[] { SampleRouteSelection.TwoWireResistance },
                SampleTopology.FourWire => new[] { SampleRouteSelection.FourWireResistance },
                _ => SampleResistanceAnalysis.Selections(sample.Topology).ToArray(),
            };
            var reads = selections.Select((selection, index) =>
            {
                var sampleBinding = new SampleRoutingBinding(sample.Id, sampleWiring!, selection);
                var route = samples.Routing.Resolve(sampleBinding);
                var read = new ReadCurrentResistanceStep(new(path + ".read." + index), source.Device, meter, CurrentReference.ProgrammedSetpoint,
                    new(path + ".V_V"), new(path + ".I_A"), new(path + ".R_ohms"), threshold, expected,
                    new(route.Matrix, route.Closed), MeasurementEnvironment(path, c), new CurrentResistanceRetry(true, settle));
                return new ReadSampleResistanceStep(read, sampleBinding, samples.RouteSettle, acquisitionSettle: settle);
            }).ToArray();
            steps.Add(reads.Length == 1 ? reads[0] : new SampleAnalysisStep(new(path + ".analysis"), reads,
                SampleResistanceAnalysis.Kinds(sample.Topology).Select(kind => new SampleDerivedColumn(kind, new(path + "." + kind)))));
            return new SequenceStep(new(path), steps);
        }
        steps.Add(new ReadCurrentResistanceStep(new(path + ".read"), source.Device, meter, CurrentReference.ProgrammedSetpoint,
            new(path + ".V_V"), new(path + ".I_A"), new(path + ".R_ohms"), threshold, expected,
            binding is null ? null : new(binding.Route.Device, binding.Route.Closed), MeasurementEnvironment(path, c), new CurrentResistanceRetry(!managed, managed ? TimeSpan.Zero : settle)));
        if (!managed) steps.Add(new SourceOutputStep(new(path + ".done"), source.Device, false));
        return new SequenceStep(new(path), steps);
    }
}

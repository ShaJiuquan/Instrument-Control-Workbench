using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep CurrentResistance(string name, JsonObject p, string path, ImportContext c)
    {
        Alias(p, "mode", "meas_mode"); Alias(p, "current_a", "current", "excitation_a", "i_a");
        Alias(p, "compliance_v", "compliance"); Alias(p, "source_range_a", "range_a", "current_range_a");
        Alias(p, "meter_range_v", "sense_range_v", "voltage_range_v"); Alias(p, "nplc", "sense_nplc", "meter_nplc");
        Alias(p, "sense_channel", "k2182_channel"); Alias(p, "source_managed", "source_managed_externally");
        Alias(p, "settle_s", "measure_settle_s"); Alias(p, "meter_instrument", "sense_instrument");
        Fields(p, "meas_type", "name", "mode", "current_a", "compliance_v", "source_range_a", "meter_range_v", "nplc", "sense_channel", "source_managed", "settle_s",
            "channel", "source_instrument", "meter_instrument", "record_temperature", "record_field", "record_angle");
        if (name != "meas_r") throw new NotSupportedException("Independent current resistance sample wiring/analysis is not yet migrated.");
        Choice(p, "mode", "curr_dc", "curr_dc");
        var sourceName = Text(p, "source_instrument", "k6221").Trim();
        var meterName = Text(p, "meter_instrument", "k2182").Trim();
        var source = CurrentSource(new JsonObject { ["instrument"] = sourceName }, c);
        if (!c.Bindings.Instruments.TryGetValue(meterName, out var meter) || !c.Bindings.StandaloneVoltageMeters.Contains(meter) || meter == source.Device)
            throw new NotSupportedException("curr_dc requires an explicitly bound independent voltage meter.");
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
        var channel = Text(p, "channel", "").Trim(); if (channel == "" && scope.Device == source.Device) channel = scope.Channel;
        if (channel == "") channel = "ch1";
        ImportChannelRoute? binding = null;
        if (!channel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!c.Bindings.Channels.TryGetValue(channel, out binding) || binding.Source != source.Device || binding.VoltageMeter != meter)
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
            steps.Add(new SourceOutputStep(new(path + ".on"), source.Device, true)); steps.Add(delay);
        }
        else
        {
            c.Metadata[path + ".curr_dc_managed_compliance_v_unused"] = new(compliance, PhysicalUnit.Volt);
            if (sourceRange is { } ignoredRange) c.Metadata[path + ".curr_dc_managed_source_range_a_unused"] = ignoredRange;
            c.Metadata[path + ".curr_dc_managed_settle_s_unused"] = new(settle.TotalSeconds, PhysicalUnit.Second);
        }
        steps.Add(new ReadCurrentResistanceStep(new(path + ".read"), source.Device, meter, CurrentReference.ProgrammedSetpoint,
            new(path + ".V_V"), new(path + ".I_A"), new(path + ".R_ohms"), threshold, expected,
            binding is null ? null : new(binding.Route.Device, binding.Route.Closed), MeasurementEnvironment(path, c)));
        if (!managed) steps.Add(new SourceOutputStep(new(path + ".done"), source.Device, false));
        return new SequenceStep(new(path), steps);
    }
}

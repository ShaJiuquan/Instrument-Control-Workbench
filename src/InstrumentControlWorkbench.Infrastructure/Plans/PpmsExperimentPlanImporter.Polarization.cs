using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep PolarizationReadout(JsonObject original, string path, ImportContext c)
    {
        var p = (JsonObject)original.DeepClone();
        Alias(p, "pol_voltage_v", "pol_voltage"); Alias(p, "read_voltage_v", "read_voltage"); Alias(p, "pol_duration_s", "pol_duration");
        Alias(p, "inter_pulse_voltage_v", "inter_pulse_voltage"); Alias(p, "inter_pulse_duration_s", "inter_pulse_duration"); Alias(p, "read_delay_s", "read_delay");
        Alias(p, "compliance_a", "compliance_current_a"); Alias(p, "sensing_mode", "sense_mode"); Alias(p, "meter_range_a", "sense_range_a");
        Fields(p, "meas_type", "name", "instrument", "channel", "pol_voltage_v", "read_voltage_v", "pol_duration_s", "inter_pulse_voltage_v", "inter_pulse_duration_s", "read_delay_s", "compliance_a", "sensing_mode", "meter_range_a", "nplc", "maximum_attempts");
        var injected = c.PolarizationChildren.TryGetValue(path, out var pointContext);
        if (injected && pointContext.Channel is { } inheritedChannel) p["channel"] = inheritedChannel;
        var source = Source(p, c, SourceFunction.Voltage);
        if (source.Sense.Function != SenseFunction.Current || c.SourceScans.Count != 0) throw new NotSupportedException("Polarization must own its prepared voltage/current source outside enclosing source scans.");
        var channel = Text(p, "channel").Trim(); // Explicit route or externally wired none.
        var mode = p["sensing_mode"] is null ? SensingMode.TwoWire : WireMode(p["sensing_mode"]);
        if (mode != source.Sense.Mode) throw new NotSupportedException("Polarization sensing mode differs from frozen source wiring.");
        if (mode == SensingMode.FourWire && !channel.Equals("none", StringComparison.OrdinalIgnoreCase) && c.Bindings.ChannelProfiles is null)
            throw new NotSupportedException("Four-wire polarization channels require frozen named source/sense wiring profiles.");
        var selector = RangeSelector(p, "meter_range_a", PhysicalUnit.Ampere);
        var range = selector is { } chosen ? Keithley2450SenseRanges.Resolve(SenseFunction.Current, chosen) : (Quantity?)null;
        var routing = MeterRouting(p, path, source.Device, c, independent: false);
        var readVoltage = new Quantity(Number(p, "read_voltage_v"), PhysicalUnit.Volt);
        var pulse = new ReadPolarizationStep(new(path), source.Device, injected ? readVoltage : new(Number(p, "pol_voltage_v"), PhysicalUnit.Volt), Duration(p, "pol_duration_s", 1),
            new(Number(p, "inter_pulse_voltage_v", 0), PhysicalUnit.Volt), Duration(p, "inter_pulse_duration_s", 0), readVoltage, Duration(p, "read_delay_s", .1),
            new(Number(p, "compliance_a", .001), PhysicalUnit.Ampere), new(SenseFunction.Current, mode, Number(p, "nplc", 1), range is null, range),
            new(path + ".voltage"), new(path + ".current"), new(path + ".attempts"), Integer(p, "maximum_attempts", 8), routing.RequiredRoute, MeasurementEnvironment(path, c), injected ? new ParameterId(path + ".polarization_voltage") : null, injected ? new VariableValue(pointContext.Variable) : null);
        if (routing.Setup is null) return pulse;
        return new SequenceStep(new(path + ".routed"), [new SourceOutputStep(new(path + ".off"), source.Device, false), routing.Setup, pulse]);
    }
}

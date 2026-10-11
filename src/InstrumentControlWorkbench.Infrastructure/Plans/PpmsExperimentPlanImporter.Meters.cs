using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep StandaloneVoltage(JsonObject p, string path, InstrumentId device, ImportContext c)
    {
        if (p["meter_range_a"] is not null) throw new NotSupportedException("Voltage measurement cannot select a current range.");
        if (Text(p, "sensing_mode", "2wire").Trim().ToLowerInvariant() != "2wire")
            throw new NotSupportedException("K2182 passive voltage input has no SMU remote-sense setting.");
        if (Number(p, "sense_channel", 1) != 1) throw new NotSupportedException("PPMS channel_voltage selects input 1; use a native plan for input 2.");
        var route = Text(p, "channel", "").Trim();
        if (route != "" && !route.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Routed standalone voltage acquisition is not yet migrated; use an explicitly wired channel='none' plan.");
        var selector = RangeSelector(p, "meter_range_v", PhysicalUnit.Volt);
        var range = selector is { } selected ? Keithley2182Settings.ResolveRange(1, selected) : (Quantity?)null;
        var nplc = Number(p, "nplc", 1); var enabled = Boolean(p, "filter_enabled", false);
        var count = Number(p, "filter_count", 10); var window = Number(p, "filter_window_percent", 0.1);
        if (count is < 2 or > 100 || count != Math.Truncate(count)) throw new ArgumentException("PPMS filter count requires an integer from 2 to 100.");
        if (enabled && nplc * count > 800) throw new ArgumentException("NPLC times filter count exceeds the PPMS K2182 read budget.");
        _ = new Keithley2182Settings(1, range, nplc, enabled, (int)count, AveragingMode.Repeat, window);
        var steps = new List<ExperimentStep>
        {
            Write(path + ".input", device, Keithley2182Parameters.Channel, 1),
            Write(path + ".range", device, range is null ? Keithley2182Parameters.EnableAutoRange : Keithley2182Parameters.ManualRange, range?.Value ?? 1),
            Write(path + ".nplc", device, Keithley2182Parameters.Nplc, nplc),
            Write(path + ".filter_off", device, Keithley2182Parameters.FilterEnabled, 0),
        };
        if (enabled)
        {
            steps.Add(Write(path + ".filter_count", device, Keithley2182Parameters.FilterCount, count));
            steps.Add(Write(path + ".filter_mode", device, Keithley2182Parameters.FilterMode, (int)AveragingMode.Repeat));
            steps.Add(Write(path + ".filter_window", device, Keithley2182Parameters.FilterWindowPercent, window));
            steps.Add(Write(path + ".filter_on", device, Keithley2182Parameters.FilterEnabled, 1));
        }
        steps.Add(new ReadStep(new(path + ".read"), [new(new(path + ".value"), new(device, Keithley2182Parameters.Voltage.Id))], MeasurementEnvironment(path, c)));
        return new SequenceStep(new(path), steps);
    }
}

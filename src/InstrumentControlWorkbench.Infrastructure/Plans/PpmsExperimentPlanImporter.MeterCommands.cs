using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep MeterCommand(string name, JsonObject p, string path, ImportContext c)
    {
        if (!c.Bindings.Instruments.TryGetValue("k2182", out var device) || !c.Bindings.StandaloneVoltageMeters.Contains(device))
            throw new NotSupportedException("K2182 commands require an explicit standalone voltage-meter binding.");
        if (name == "k2182.set_nplc")
        { Fields(p, "nplc"); return Write(path, device, Keithley2182Parameters.Nplc, Number(p, "nplc")); }
        var steps = new List<ExperimentStep>();
        int? channel = null;
        if (p["channel"] is not null)
        {
            var selected = Number(p, "channel");
            if (selected is not (1 or 2)) throw new ArgumentException("K2182 channel requires integer 1 or 2.");
            channel = (int)selected; steps.Add(Write(path + ".input", device, Keithley2182Parameters.Channel, selected));
        }
        if (name == "k2182.set_range")
        {
            Alias(p, "range_v", "range"); Fields(p, "channel", "range_v"); var selector = Number(p, "range_v");
            if (channel is { } input) _ = Keithley2182Settings.ResolveRange(input, new(selector, PhysicalUnit.Volt));
            steps.Add(Write(path + ".range", device, Keithley2182Parameters.RangeSelector, selector));
        }
        else
        {
            JsonObject config;
            if (p["config"] is { } nested) { Fields(p, "channel", "config"); config = Object(nested); }
            else { config = p; config.Remove("config"); }
            Alias(config, "enabled", "filter_enabled"); Alias(config, "count", "filter_count");
            Alias(config, "filter_type", "type"); Alias(config, "window", "filter_window");
            Fields(config, [.. ReferenceEquals(config, p) ? new[] { "channel" } : [], "enabled", "count", "filter_type", "window"]);
            if (!config.ContainsKey("enabled")) throw new FormatException("enabled is required.");
            var enabled = Boolean(config, "enabled", false);
            double? count = config["count"] is null ? null : Number(config, "count");
            if (count is { } n && (n < 0 || n != Math.Truncate(n) || n > ulong.MaxValue || enabled && n is < 2 or > 100))
                throw new ArgumentException("Enabled PPMS filter count requires an integer from 2 to 100.");
            double? window = config["window"] is null ? null : Number(config, "window");
            if (enabled && window is { } w && w is < 0 or > 10) throw new ArgumentException("Filter window requires 0..10 percent.");
            var mode = config["filter_type"] is null ? "" : Text(config, "filter_type").Trim().ToUpperInvariant();
            if (mode is not ("" or "MOV" or "REP")) throw new ArgumentException("K2182 filter type requires MOV or REP.");
            // A disabled filter ignores its optional numeric controls in PPMS.
            // When enabled, omitted controls retain the target input's registers.
            if (enabled)
            {
                if (count is { } amount) steps.Add(Write(path + ".count", device, Keithley2182Parameters.FilterCount, amount));
                if (mode != "") steps.Add(Write(path + ".mode", device, Keithley2182Parameters.FilterMode, (int)(mode == "MOV" ? AveragingMode.Moving : AveragingMode.Repeat)));
                if (window is { } percent) steps.Add(Write(path + ".window", device, Keithley2182Parameters.FilterWindowPercent, percent));
            }
            steps.Add(Write(path + ".enabled", device, Keithley2182Parameters.FilterEnabled, enabled ? 1 : 0));
        }
        return new SequenceStep(new(path), steps);
    }
}

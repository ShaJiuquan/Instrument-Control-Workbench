using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static SetParameterStep PicoWrite(string path, InstrumentId device, ParameterDescriptor parameter, double value)
    {
        Keithley6485Parameters.ValidateWrite(parameter.Id, new(value, parameter.Unit));
        return Write(path, device, parameter, value);
    }
    private static ExperimentStep PicoammeterCommand(string name, JsonObject p, string path, ImportContext c)
    {
        if (!c.Bindings.Instruments.TryGetValue("k6485", out var device) || !c.Bindings.StandaloneCurrentMeters.Contains(device))
            throw new NotSupportedException("K6485 commands require an explicit standalone current-meter binding.");
        switch (name)
        {
            case "k6485.set_nplc":
                Fields(p, "nplc"); return PicoWrite(path, device, Keithley6485Parameters.Nplc, Number(p, "nplc"));
            case "k6485.set_range":
                Fields(p, "range");
                return p["range"] is null ? PicoWrite(path, device, Keithley6485Parameters.EnableAutoRange, 1) : PicoWrite(path, device, Keithley6485Parameters.ManualRange, Number(p, "range"));
            case "k6485.set_auto_range":
                Alias(p, "enabled", "auto_range"); Fields(p, "enabled", "range_a");
                if (p["enabled"] is null) throw new FormatException("enabled is required.");
                if (Boolean(p, "enabled", false))
                {
                    if (p["range_a"] is not null) throw new ArgumentException("An enabled autorange cannot specify a manual span.");
                    return PicoWrite(path, device, Keithley6485Parameters.EnableAutoRange, 1);
                }
                return PicoWrite(path, device, Keithley6485Parameters.ManualRange, Number(p, "range_a"));
            case "k6485.set_zero_check":
            case "k6485.set_zero_correct":
                Fields(p, "enabled"); if (p["enabled"] is null) throw new FormatException("enabled is required.");
                return PicoWrite(path, device, name.EndsWith("check", StringComparison.Ordinal) ? Keithley6485Parameters.ZeroCheck : Keithley6485Parameters.ZeroCorrect, Boolean(p, "enabled", false) ? 1 : 0);
            default:
                Fields(p, "enabled", "count", "mode"); var steps = new List<ExperimentStep>();
                if (p["count"] is not null) steps.Add(PicoWrite(path + ".count", device, Keithley6485Parameters.AveragingCount, Number(p, "count")));
                if (p["mode"] is not null)
                {
                    var mode = Text(p, "mode").Trim().ToUpperInvariant();
                    if (mode != "") steps.Add(PicoWrite(path + ".mode", device, Keithley6485Parameters.AveragingMode,
                        (int)(mode switch { "MOV" => AveragingMode.Moving, "REP" => AveragingMode.Repeat, _ => throw new ArgumentException("K6485 averaging mode requires MOV or REP.") })));
                }
                if (p["enabled"] is not null) steps.Add(PicoWrite(path + ".enabled", device, Keithley6485Parameters.AveragingEnabled, Boolean(p, "enabled", false) ? 1 : 0));
                if (steps.Count == 0) throw new ArgumentException("K6485 averaging command requires a configuration change.");
                return new SequenceStep(new(path), steps);
        }
    }
    // Direct passive current acquisition is an explicit C# extension. Rust's
    // channel_current accepts only K2450; never reinterpret a matrix channel.
    private static ExperimentStep StandaloneCurrent(JsonObject p, string path, InstrumentId device, ImportContext c)
    {
        var channel = Text(p, "channel", "none").Trim();
        if (!channel.Equals("none", StringComparison.OrdinalIgnoreCase) || p["meter_range_v"] is not null || p["filter_window_percent"] is not null ||
            Text(p, "sensing_mode", "2wire").Trim().ToLowerInvariant() != "2wire" || Number(p, "sense_channel", 1) != 1)
            throw new NotSupportedException("K6485 current acquisition is direct, has no matrix/voltage/filter-window/remote-sense/input-selector setting.");
        if (c.SourceScans.Count != 0) throw new NotSupportedException("Standalone K6485 cannot inherit channel routing.");
        var range = p["meter_range_a"] is null ? (Quantity?)null : new Quantity(Number(p, "meter_range_a"), PhysicalUnit.Ampere);
        var nplc = Number(p, "nplc", 1); var count = Number(p, "filter_count", 10); var enabled = Boolean(p, "filter_enabled", false);
        if (count != Math.Truncate(count) || count is < 2 or > 100 || enabled && nplc * count > 500) throw new ArgumentException("PPMS current filter requires count 2..100 and integration budget <=500 PLC.");
        _ = new Keithley6485Settings(range, nplc, false, false, enabled, (int)count, AveragingMode.Repeat);
        var steps = new List<ExperimentStep>
        {
            PicoWrite(path + ".range", device, range is null ? Keithley6485Parameters.EnableAutoRange : Keithley6485Parameters.ManualRange, range?.Value ?? 1),
            PicoWrite(path + ".nplc", device, Keithley6485Parameters.Nplc, nplc),
            PicoWrite(path + ".filter_off", device, Keithley6485Parameters.AveragingEnabled, 0),
        };
        if (enabled)
        {
            steps.Add(PicoWrite(path + ".count", device, Keithley6485Parameters.AveragingCount, count));
            steps.Add(PicoWrite(path + ".mode", device, Keithley6485Parameters.AveragingMode, (int)AveragingMode.Repeat));
            steps.Add(PicoWrite(path + ".filter_on", device, Keithley6485Parameters.AveragingEnabled, 1));
        }
        // Zero check is deliberately left as observed. An authored command
        // must disable it before reading; acquisition never hides this action.
        steps.Add(new ReadStep(new(path + ".read"), [new(new(path + ".value"), new(device, Keithley6485Parameters.Current.Id))], MeasurementEnvironment(path, c)));
        return new SequenceStep(new(path), steps);
    }
}

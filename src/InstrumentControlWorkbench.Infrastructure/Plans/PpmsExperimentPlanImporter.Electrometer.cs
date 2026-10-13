using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ElectrometerFunction ElectrometerFunctionName(string name) => name.Trim().ToUpperInvariant() switch
    {
        "CURR" or "CURRENT" => ElectrometerFunction.Current,
        "VOLT" or "VOLTAGE" => ElectrometerFunction.Voltage,
        "RES" or "RESISTANCE" => ElectrometerFunction.Resistance,
        "CHAR" or "CHARGE" => ElectrometerFunction.Charge,
        _ => throw new ArgumentException("Electrometer function requires CURRENT/CURR, VOLTAGE/VOLT, RESISTANCE/RES or CHARGE/CHAR."),
    };
    private static InstrumentId ElectrometerDevice(string alias, ImportContext c)
        => c.Bindings.Instruments.TryGetValue(alias, out var device) && c.Bindings.Electrometers.Contains(device) ? device : throw new NotSupportedException("K6514 requires an explicitly bound electrometer.");
    private static SetParameterStep ElectrometerWrite(string path, InstrumentId device, ParameterDescriptor parameter, double value)
    {
        Keithley6514Parameters.ValidateWrite(parameter.Id, new(value, parameter.Unit)); return Write(path, device, parameter, value);
    }
    private static ExperimentStep ElectrometerCommand(string name, JsonObject p, string path, ImportContext c)
    {
        var device = ElectrometerDevice("k6514", c);
        switch (name)
        {
            case "k6514.set_function":
                Fields(p, "function"); return ElectrometerWrite(path, device, Keithley6514Parameters.Function, (int)ElectrometerFunctionName(Text(p, "function")));
            case "k6514.set_nplc":
                Fields(p, "function", "nplc"); _ = ElectrometerFunctionName(Text(p, "function"));
                // Manufacturer specifies one shared NPLC register for all modes,
                // including charge; unlike Rust, this is not a charge-mode no-op.
                return ElectrometerWrite(path, device, Keithley6514Parameters.Nplc, Number(p, "nplc"));
            case "k6514.set_range":
                Fields(p, "function", "range"); var rangeFunction = ElectrometerFunctionName(Text(p, "function"));
                return p["range"] is null ? ElectrometerWrite(path, device, Keithley6514Parameters.AutoRange(rangeFunction), 1) : ElectrometerWrite(path, device, Keithley6514Parameters.RangeSelector(rangeFunction), Number(p, "range"));
            case "k6514.set_auto_range":
                Alias(p, "enabled", "auto_range"); Fields(p, "function", "enabled"); if (p["enabled"] is null) throw new FormatException("enabled is required.");
                return ElectrometerWrite(path, device, Keithley6514Parameters.AutoRange(ElectrometerFunctionName(Text(p, "function"))), Boolean(p, "enabled", false) ? 1 : 0);
            case "k6514.set_zero_check":
            case "k6514.set_zero_correct":
                Fields(p, "enabled"); if (p["enabled"] is null) throw new FormatException("enabled is required.");
                return ElectrometerWrite(path, device, name.EndsWith("check", StringComparison.Ordinal) ? Keithley6514Parameters.ZeroCheck : Keithley6514Parameters.ZeroCorrect, Boolean(p, "enabled", false) ? 1 : 0);
            default:
                Fields(p, "enabled", "count", "mode"); var steps = new List<ExperimentStep>();
                if (p["count"] is not null) steps.Add(ElectrometerWrite(path + ".count", device, Keithley6514Parameters.AveragingCount, Number(p, "count")));
                if (p["mode"] is not null)
                {
                    var mode = Text(p, "mode").Trim().ToUpperInvariant();
                    if (mode != "") steps.Add(ElectrometerWrite(path + ".mode", device, Keithley6514Parameters.AveragingMode, (int)(mode switch { "MOV" => AveragingMode.Moving, "REP" => AveragingMode.Repeat, _ => throw new ArgumentException("Electrometer averaging mode requires MOV or REP.") })));
                }
                if (p["enabled"] is not null) steps.Add(ElectrometerWrite(path + ".enabled", device, Keithley6514Parameters.AveragingEnabled, Boolean(p, "enabled", false) ? 1 : 0));
                if (steps.Count == 0) throw new ArgumentException("Electrometer averaging command requires a change.");
                return new SequenceStep(new(path), steps);
        }
    }
    // Explicit direct-acquisition extension. No implicit route or source control.
    private static ExperimentStep StandaloneElectrometer(string name, JsonObject p, string path, InstrumentId device, ImportContext c)
    {
        Fields(p, "meas_type", "name", "instrument", "channel", "nplc", "meter_range_a", "meter_range_v", "meter_range_ohm", "meter_range_c", "filter_enabled", "filter_count", "sensing_mode", "sense_channel");
        if (!Text(p, "channel", "none").Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || c.SourceScans.Count != 0 ||
            Text(p, "sensing_mode", "2wire").Trim().ToLowerInvariant() != "2wire" || Number(p, "sense_channel", 1) != 1)
            throw new NotSupportedException("K6514 acquisition is direct and cannot inherit matrix/source scans or select remote sense/another input.");
        var function = name switch { "measure_current" => ElectrometerFunction.Current, "measure_voltage" => ElectrometerFunction.Voltage, "measure_charge" => ElectrometerFunction.Charge, "measure_electrometer_resistance" => ElectrometerFunction.Resistance, _ => throw new NotSupportedException("Unknown electrometer acquisition.") };
        var rangeKey = function switch { ElectrometerFunction.Current => "meter_range_a", ElectrometerFunction.Voltage => "meter_range_v", ElectrometerFunction.Resistance => "meter_range_ohm", _ => "meter_range_c" };
        foreach (var key in new[] { "meter_range_a", "meter_range_v", "meter_range_ohm", "meter_range_c" }) if (key != rangeKey && p[key] is not null) throw new NotSupportedException("Electrometer range unit must match its function.");
        var nplc = Number(p, "nplc", 1); var count = Number(p, "filter_count", 10); var enabled = Boolean(p, "filter_enabled", false);
        if (count != Math.Truncate(count) || count is < 2 or > 100 || enabled && nplc * count > 500) throw new ArgumentException("Enabled current acquisition filter budget requires <=500PLC with count2..100.");
        var steps = new List<ExperimentStep> { ElectrometerWrite(path + ".function", device, Keithley6514Parameters.Function, (int)function),
            p[rangeKey] is null ? ElectrometerWrite(path + ".range", device, Keithley6514Parameters.AutoRange(function), 1) : ElectrometerWrite(path + ".range", device, Keithley6514Parameters.RangeSelector(function), Number(p, rangeKey)),
            ElectrometerWrite(path + ".nplc", device, Keithley6514Parameters.Nplc, nplc), ElectrometerWrite(path + ".filter_off", device, Keithley6514Parameters.AveragingEnabled, 0) };
        if (enabled)
        {
            steps.Add(ElectrometerWrite(path + ".count", device, Keithley6514Parameters.AveragingCount, count));
            steps.Add(ElectrometerWrite(path + ".mode", device, Keithley6514Parameters.AveragingMode, (int)AveragingMode.Repeat));
            steps.Add(ElectrometerWrite(path + ".filter_on", device, Keithley6514Parameters.AveragingEnabled, 1));
        }
        // Zero check remains observed; sample reading requires an explicit OFF.
        steps.Add(new ReadStep(new(path + ".read"), [new(new(path + ".value"), new(device, Keithley6514Parameters.Reading(function).Id))], MeasurementEnvironment(path, c)));
        return new SequenceStep(new(path), steps);
    }
}

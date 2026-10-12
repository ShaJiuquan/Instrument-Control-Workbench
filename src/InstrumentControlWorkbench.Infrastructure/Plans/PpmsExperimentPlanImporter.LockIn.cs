using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep Sr830Command(string name, JsonObject p, string path, ImportContext c)
    {
        var input = name == "sr830.set_input_config";
        var parameter = name switch
        {
            "sr830.set_sensitivity" => Sr830Parameters.Sensitivity,
            "sr830.set_time_constant" => Sr830Parameters.TimeConstant,
            "sr830.set_filter_slope" => Sr830Parameters.FilterSlope,
            _ => Sr830Parameters.InputMode,
        };
        var key = name switch { "sr830.set_sensitivity" => "sensitivity_v", "sr830.set_time_constant" => "time_constant_s", "sr830.set_filter_slope" => "filter_slope_db", _ => "input_config" };
        var alias = input ? "config" : key switch { "sensitivity_v" => "sensitivity", "time_constant_s" => "time_constant", _ => "filter_slope" };
        Fields(p, "instrument", key, alias);
        if (p.ContainsKey(key) && p.ContainsKey(alias)) throw new FormatException("Choose one SR830 parameter alias.");
        var selectedKey = p.ContainsKey(alias) ? alias : key;
        var instrument = Text(p, "instrument", "sr830").Trim();
        if (!c.Bindings.Instruments.TryGetValue(instrument, out var device)) throw new NotSupportedException("SR830 commands require an explicit instrument alias.");
        var mode = input ? Text(p, selectedKey).Trim().ToLowerInvariant() switch
        {
            "a" => Sr830InputMode.VoltageA,
            "a-b" => Sr830InputMode.VoltageAB,
            "i-1m" => Sr830InputMode.Current1M,
            "i-100m" => Sr830InputMode.Current100M,
            _ => throw new NotSupportedException("Unsupported SR830 input mode."),
        } : Sr830InputMode.VoltageA;
        var value = new Quantity(input ? (int)mode : Number(p, selectedKey), parameter.Unit); Sr830Parameters.ValidateWrite(parameter.Id, value);
        return new SetParameterStep(new(path), new(device, parameter.Id), new ConstantValue(value));
    }
}

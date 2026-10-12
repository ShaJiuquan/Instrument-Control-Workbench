using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static JsonObject VoltageScanParameters(JsonObject parameters)
    {
        var p = (JsonObject)parameters.DeepClone();
        var aliases = new[] { "source_range_v", "range_v", "voltage_range_v" };
        var present = aliases.Where(p.ContainsKey).ToArray();
        if (present.Length > 1) throw new FormatException("Choose one voltage scan range alias.");
        if (present.Length == 1 && present[0] != aliases[0]) { p[aliases[0]] = p[present[0]]?.DeepClone(); p.Remove(present[0]); }
        return p;
    }
    private static Quantity? SourceRange(JsonObject p, SourceFunction function)
    {
        var key = function == SourceFunction.Voltage ? "source_range_v" : "source_range_a";
        var selector = RangeSelector(p, key, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere);
        return selector is { } value ? Keithley2450SourceRanges.Resolve(function, value) : null;
    }
    private static Quantity? RangeSelector(JsonObject p, string key, PhysicalUnit unit)
    {
        if (p[key] is null || p[key] is JsonValue text && text.TryGetValue<string>(out var word) && word.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var value = ResistanceNumber(p, key, 0);
        if (value <= 0) throw new FormatException($"{key} requires a positive finite range selector or auto.");
        return new(value, unit);
    }
    private static SetParameterStep SourceRangeStep(string path, SourcePreparation source, Quantity? range)
    {
        var parameter = range is null ? Keithley2450Parameters.EnableSourceAutoRange :
            source.Function == SourceFunction.Voltage ? Keithley2450Parameters.ManualVoltageRange : Keithley2450Parameters.ManualCurrentRange;
        return new(new(path + ".source_range"), new(source.Device, parameter.Id), new ConstantValue(range ?? new(1, PhysicalUnit.Dimensionless)));
    }
}

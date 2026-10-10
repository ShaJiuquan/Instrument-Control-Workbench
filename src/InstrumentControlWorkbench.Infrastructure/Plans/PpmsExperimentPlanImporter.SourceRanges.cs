using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static Quantity? SourceRange(JsonObject p, SourceFunction function)
    {
        var key = function == SourceFunction.Voltage ? "source_range_v" : "source_range_a";
        if (p[key] is null || p[key] is JsonValue text && text.TryGetValue<string>(out var word) && word.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var value = ResistanceNumber(p, key, 0);
        return Keithley2450SourceRanges.Resolve(function, new(value, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere));
    }
    private static SetParameterStep SourceRangeStep(string path, SourcePreparation source, Quantity? range)
    {
        var parameter = range is null ? Keithley2450Parameters.EnableSourceAutoRange :
            source.Function == SourceFunction.Voltage ? Keithley2450Parameters.ManualVoltageRange : Keithley2450Parameters.ManualCurrentRange;
        return new(new(path + ".source_range"), new(source.Device, parameter.Id), new ConstantValue(range ?? new(1, PhysicalUnit.Dimensionless)));
    }
}

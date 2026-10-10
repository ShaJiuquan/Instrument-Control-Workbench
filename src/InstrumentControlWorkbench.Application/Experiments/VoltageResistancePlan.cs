using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

// One-point recipe adapter. A scan-managed read uses ReadResistanceStep inside
// a typed scan; it never changes source level or turns output off on each point.
public static class VoltageResistancePlan
{
    public static ExperimentPlan Create(InstrumentId device, VoltageResistanceRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new(recipe.Name, new SequenceStep(new("voltage_resistance"),
            [new SourceOutputStep(new("output_on"), device, true),
             new DelayStep(new("settle"), recipe.Settle),
             new ReadResistanceStep(new("resistance_read"), device, SourceFunction.Voltage,
                 new("voltage"), new("current"), new("resistance"), recipe.MinimumCurrent),
             new SourceOutputStep(new("output_off"), device, false)]),
            [new(device, SourceFunction.Voltage, recipe.Voltage, recipe.Compliance, recipe.Sense)],
            new Dictionary<string, Quantity>
            {
                ["voltage"] = recipe.Voltage,
                ["compliance"] = recipe.Compliance,
                ["settle"] = new(recipe.Settle.TotalSeconds, PhysicalUnit.Second),
                ["minimum_current"] = recipe.MinimumCurrent
            });
    }
}

using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

// Hall and vdP recipes acquire role-specific raw V/I/R rows. Derived Hall/sheet
// quantities require a separate analysis stage; no synthetic result is substituted.
public static class SampleResistancePlan
{
    public static ExperimentPlan Create(SampleResistanceRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new(recipe.Name, new SequenceStep(new("sample_measurement"), recipe.Selections.Select((selection, index) =>
            new ReadSampleResistanceStep(new(new($"sample_read_{index + 1}"), recipe.Source.Device, recipe.Source.Function,
                new("voltage"), new("current"), new("resistance"), recipe.MinimumCurrent), new(recipe.Sample, recipe.Wiring, selection), recipe.Settle))),
            [recipe.Source], new Dictionary<string, Quantity>
            {
                ["settle"] = new(recipe.Settle.TotalSeconds, PhysicalUnit.Second),
                ["minimum_current"] = recipe.MinimumCurrent,
            }, recipe.Routing);
    }
}

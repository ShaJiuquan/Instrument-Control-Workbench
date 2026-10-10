using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Application.Experiments;

public static class SampleResistanceAnalysisPlan
{
    public static ExperimentPlan Create(SampleResistanceRecipe recipe)
    {
        var raw = SampleResistancePlan.Create(recipe);
        var sample = recipe.Routing.Samples.Single(s => s.Id == recipe.Sample);
        var columns = SampleResistanceAnalysis.Kinds(sample.Topology).Select(kind => new SampleDerivedColumn(kind, new(kind switch
        {
            SampleDerivedQuantity.LongitudinalResistance => "r_xx",
            SampleDerivedQuantity.TransverseResistance => "r_xy",
            SampleDerivedQuantity.HorizontalMeanResistance => "r_h_mean",
            SampleDerivedQuantity.VerticalMeanResistance => "r_v_mean",
            SampleDerivedQuantity.SheetResistance => "r_sheet",
            SampleDerivedQuantity.Resistivity => "rho_xx",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        })));
        var group = new SampleAnalysisStep(new("sample_analysis"), ((SequenceStep)raw.Root).Children.Cast<ReadSampleResistanceStep>(), columns);
        return new(raw.Name, group, raw.Preparations, raw.Metadata, raw.Routing);
    }
}

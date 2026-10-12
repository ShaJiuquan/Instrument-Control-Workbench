using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed partial class ExperimentPlanWorkspace
{
    private ExperimentPlan CoordinateIndependentSampleGuards(ExperimentPlan original, ExperimentPlan candidate, SampleId? sampleId)
    {
        var structure = documents as IExperimentPlanStructureDocument ?? throw new NotSupportedException("This document adapter does not support atomic sample guard coordination.");
        // The structure kind is the version-1 document contract; decode matching
        // wrappers in one pass instead of reserializing per acquisition.
        var ids = structure.DescribeStructure(original).Where(node => node.Kind == "read_independent_sample_resistance").Select(node => node.Id).ToArray();
        var replacements = new List<ExperimentStep>();
        foreach (var step in structure.ReadSteps(original, ids))
        {
            if (step is not ReadSampleResistanceStep { Acquisition: ReadCurrentResistanceStep acquisition } sample || sampleId is not null && !StringComparer.OrdinalIgnoreCase.Equals(sample.Binding.Sample.Value, sampleId.Value)) continue;
            if (sample.RoutingManagedExternally) throw new ArgumentException("Only internally managed sample acquisitions can coordinate their guards.");
            var oldRoute = original.Routing!.Resolve(sample.Binding); var newRoute = candidate.Routing!.Resolve(sample.Binding);
            if (!Equals(acquisition.RequiredRoute, new MatrixRouteExpectation(oldRoute.Matrix, oldRoute.Closed))) throw new ArgumentException("The previous sample guard does not match its original catalog.");
            if (acquisition.Source != newRoute.SourceMeter || acquisition.Meter != newRoute.VoltageMeter) throw new ArgumentException("Guard coordination cannot change acquisition source or meter roles.");
            if (Equals(acquisition.RequiredRoute, new MatrixRouteExpectation(newRoute.Matrix, newRoute.Closed))) continue;
            runs.ValidateSampleRoutes(candidate.Routing, sample.Binding.Sample, sample.Binding.Wiring);
            var updated = new ReadCurrentResistanceStep(acquisition.Id, acquisition.Source, acquisition.Meter, acquisition.CurrentReference,
                acquisition.VoltageColumn, acquisition.CurrentColumn, acquisition.ResistanceColumn, acquisition.MinimumCurrent,
                acquisition.ExpectedProgrammedCurrent, new(newRoute.Matrix, newRoute.Closed), acquisition.EnvironmentReads, acquisition.Retry);
            replacements.Add(new ReadSampleResistanceStep(updated, sample.Binding, sample.Settle, sample.RoutingManagedExternally, sample.AcquisitionSettle));
        }
        return replacements.Count == 0 ? candidate : structure.ReplaceSteps(candidate, replacements);
    }
}

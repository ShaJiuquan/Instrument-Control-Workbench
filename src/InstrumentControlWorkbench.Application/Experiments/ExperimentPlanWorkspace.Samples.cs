using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record SampleProfileDetails(string Name, string OriginalId, string Material, string Substrate, string Notes, SampleGeometry Geometry);

public sealed partial class ExperimentPlanWorkspace
{
    public ValidatedPlanDocument UpdateSampleDetails(ExperimentPlan plan, SampleId sampleId, SampleProfileDetails details)
    {
        ArgumentNullException.ThrowIfNull(sampleId); ArgumentNullException.ThrowIfNull(details); ArgumentNullException.ThrowIfNull(details.Geometry);
        ArgumentNullException.ThrowIfNull(details.OriginalId); ArgumentNullException.ThrowIfNull(details.Material); ArgumentNullException.ThrowIfNull(details.Substrate); ArgumentNullException.ThrowIfNull(details.Notes);
        runs.PreviewPlan(plan);
        var routing = plan.Routing ?? throw new InvalidOperationException("The plan has no sample routing catalog.");
        var original = routing.Samples.SingleOrDefault(sample => StringComparer.OrdinalIgnoreCase.Equals(sample.Id.Value, sampleId.Value))
            ?? throw new ArgumentException("Choose an existing sample from the plan catalog.", nameof(sampleId));
        var replacement = new SampleProfile(original.Id, details.Name, original.Topology, original.Electrodes, details.Geometry, details.OriginalId, details.Material, details.Substrate, details.Notes);
        var updated = new RoutingConfiguration(routing.Matrix, routing.Channels, routing.Wirings, routing.Samples.Select(sample => ReferenceEquals(sample, original) ? replacement : sample));
        return Inspect(new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, updated, plan.CurrentSourcePreparations, plan.MaximumStepExecutions));
    }
}

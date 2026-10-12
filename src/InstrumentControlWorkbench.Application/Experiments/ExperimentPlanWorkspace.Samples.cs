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
    public ValidatedPlanDocument AddSample(ExperimentPlan plan, SampleProfile sample, WiringProfileId validationWiring)
    {
        ArgumentNullException.ThrowIfNull(sample); ArgumentNullException.ThrowIfNull(validationWiring); runs.PreviewPlan(plan);
        var routing = plan.Routing ?? throw new InvalidOperationException("Create or import a routing catalog first.");
        if (routing.Samples.Any(existing => StringComparer.OrdinalIgnoreCase.Equals(existing.Id.Value, sample.Id.Value))) throw new ArgumentException("The sample ID already exists.");
        var updated = new RoutingConfiguration(routing.Matrix, routing.Channels, routing.Wirings, routing.Samples.Append(sample));
        runs.ValidateSampleRoutes(updated, sample.Id, validationWiring);
        return Inspect(new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, updated, plan.CurrentSourcePreparations, plan.MaximumStepExecutions));
    }
    public ValidatedPlanDocument UpdateSampleDefinition(ExperimentPlan plan, SampleId sampleId, SampleProfileDetails details, IReadOnlyList<SampleElectrode> electrodes, WiringProfileId validationWiring, bool synchronizeIndependentRoutes = false)
    {
        ArgumentNullException.ThrowIfNull(electrodes); ArgumentNullException.ThrowIfNull(validationWiring);
        // Reuse canonical details admission, then replace only this sample's electrodes.
        var candidate = UpdateSampleDetails(plan, sampleId, details).Plan; var routing = candidate.Routing!;
        var original = routing.Samples.Single(sample => StringComparer.OrdinalIgnoreCase.Equals(sample.Id.Value, sampleId.Value));
        var replacement = new SampleProfile(original.Id, original.Name, original.Topology, electrodes, original.Geometry, original.OriginalId, original.Material, original.Substrate, original.Notes);
        var updated = new RoutingConfiguration(routing.Matrix, routing.Channels, routing.Wirings, routing.Samples.Select(sample => ReferenceEquals(sample, original) ? replacement : sample));
        runs.ValidateSampleRoutes(updated, original.Id, validationWiring);
        var complete = new ExperimentPlan(plan.Name, plan.Root, plan.Preparations, plan.Metadata, updated, plan.CurrentSourcePreparations, plan.MaximumStepExecutions);
        if (synchronizeIndependentRoutes) complete = CoordinateIndependentSampleGuards(plan, complete, original.Id);
        return Inspect(complete);
    }
    public ValidatedPlanDocument RemoveSample(ExperimentPlan plan, SampleId sampleId)
    {
        ArgumentNullException.ThrowIfNull(sampleId); runs.PreviewPlan(plan);
        var routing = plan.Routing ?? throw new InvalidOperationException("The plan has no sample catalog.");
        if (!routing.Samples.Any(sample => StringComparer.OrdinalIgnoreCase.Equals(sample.Id.Value, sampleId.Value))) throw new ArgumentException("The sample does not exist.");
        var updated = new RoutingConfiguration(routing.Matrix, routing.Channels, routing.Wirings, routing.Samples.Where(sample => !StringComparer.OrdinalIgnoreCase.Equals(sample.Id.Value, sampleId.Value)));
        return Inspect(new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, updated, plan.CurrentSourcePreparations, plan.MaximumStepExecutions));
    }

}

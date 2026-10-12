using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record ValidatedPlanDocument(ExperimentPlan Plan, string Text, ExperimentPlanPreview Preview);

public sealed class ExperimentPlanWorkspace(IExperimentPlanDocument documents, MeasurementRunService runs, IExperimentPlanImporter? importer = null, IRoutingProfileImporter? routingProfiles = null)
{
    public bool SupportsStructureEditing => documents is IExperimentPlanStructureDocument;
    public IReadOnlyList<DeviceDescriptor> Devices => runs.DeviceDescriptors;
    public ExperimentStep ReadStep(ExperimentPlan plan, StepId selected) => StructureDocuments.ReadStep(plan, selected);
    public ValidatedPlanDocument EditStep(ExperimentPlan plan, PlanStructureEditKind kind, StepId selected, ExperimentStep step)
    {
        runs.PreviewPlan(plan);
        return Inspect(StructureDocuments.EditStep(plan, kind, selected, step));
    }
    public IReadOnlyList<PlanStructureNode> DescribeStructure(ExperimentPlan plan) => StructureDocuments.DescribeStructure(plan);
    public string ReadFragment(ExperimentPlan plan, StepId selected) => StructureDocuments.ReadFragment(plan, selected);
    public ValidatedPlanDocument EditStructure(ExperimentPlan plan, PlanStructureEdit edit)
    {
        // Full capability/variable/budget/resource validation before the caller
        // replaces its draft. Editing performs no connection or device commands.
        runs.PreviewPlan(plan);
        return Inspect(StructureDocuments.EditStructure(plan, edit));
    }
    private IExperimentPlanStructureDocument StructureDocuments => documents as IExperimentPlanStructureDocument
        ?? throw new NotSupportedException("This plan document adapter does not support structure editing.");
    public Task<RoutingProfileCatalog> LoadRoutingProfilesAsync(string channelsPath, string samplesPath, string instrumentPortsPath, CancellationToken token)
        => (routingProfiles ?? throw new InvalidOperationException("No routing profile importer is configured.")).LoadAsync(channelsPath, samplesPath, instrumentPortsPath, token);
    public ValidatedPlanDocument Import(string text, ExperimentImportBindings bindings)
        => Inspect((importer ?? throw new InvalidOperationException("No experiment importer is configured.")).Parse(text, bindings));
    public async Task<ValidatedPlanDocument> ImportAsync(string path, ExperimentImportBindings bindings, CancellationToken token)
        => Inspect(await (importer ?? throw new InvalidOperationException("No experiment importer is configured.")).LoadAsync(path, bindings, token));
    public ValidatedPlanDocument Validate(string text) => Inspect(documents.Parse(text));
    public ValidatedPlanDocument Inspect(ExperimentPlan plan)
    {
        var preview = runs.PreviewPlan(plan);
        return new(plan, documents.Serialize(plan), preview);
    }
    public async Task<ValidatedPlanDocument> LoadAsync(string path, CancellationToken token)
        => Inspect(await documents.LoadAsync(path, token));
    public async Task<ValidatedPlanDocument> SaveAsync(string path, string text, CancellationToken token)
    {
        // Capability validation precedes file mutation as well as execution.
        var document = Validate(text);
        await documents.SaveAsync(path, document.Plan, token);
        return document;
    }
}

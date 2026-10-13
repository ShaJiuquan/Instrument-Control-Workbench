using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record ValidatedPlanDocument(ExperimentPlan Plan, string Text, ExperimentPlanPreview Preview);

public sealed partial class ExperimentPlanWorkspace(IExperimentPlanDocument documents, MeasurementRunService runs, IExperimentPlanImporter? importer = null, IRoutingProfileImporter? routingProfiles = null, ConfiguredDeviceRuntime? runtime = null)
{
    public bool SupportsConfiguredImport => importer is not null && runtime is not null;
    public bool SupportsRoutingProfileImport => routingProfiles is not null;
    public IReadOnlyList<ConfiguredImportDevice> ImportDevices => runtime is null ? Array.Empty<ConfiguredImportDevice>()
        : runtime.Registry.Devices.Select(d => new ConfiguredImportDevice(d, runtime.Configuration.Devices.Single(c => c.Id == d.Id).Category)).ToArray();
    public IReadOnlyDictionary<string, InstrumentId> ImportAliases => ConfiguredRuntime.CreateImportBindings().Instruments;
    private ConfiguredDeviceRuntime ConfiguredRuntime => runtime ?? throw new NotSupportedException("Configured runtime import is unavailable.");
    public ExperimentImportBindings CreateConfiguredImportBindings(ExperimentPlan preparations, ImportRoutingSelection? routing = null)
    {
        // The runtime and compiler must refer to the same registered device set.
        var configured = ConfiguredRuntime;
        if (!ReferenceEquals(configured.Registry.Devices, runs.DeviceDescriptors)) throw new InvalidOperationException("Import runtime differs from the execution registry.");
        runs.PreviewPlan(preparations);
        var bindings = configured.CreateImportBindings(preparations.Preparations, preparations.CurrentSourcePreparations);
        if (routing is null) return bindings;
        RequireCategory(routing.Matrix, ConfiguredDeviceCategory.Matrix);
        foreach (var wiring in routing.Wirings)
        {
            RequireCategory(wiring.Source, ConfiguredDeviceCategory.SourceMeter, ConfiguredDeviceCategory.Source);
            if (wiring.VoltageMeter is { } meter) RequireCategory(meter, ConfiguredDeviceCategory.Meter);
        }
        var channel = routing.Wirings.Single(w => w.Wiring == routing.ChannelWiring);
        return routing.Profiles.Bind(bindings, routing.Matrix, routing.Wirings.Select(w => KeyValuePair.Create(w.Wiring, w.Source)),
            routing.ChannelWiring, routing.RouteSettle, channel.VoltageMeter,
            routing.Wirings.Where(w => w.VoltageMeter is not null).Select(w => KeyValuePair.Create(w.Wiring, w.VoltageMeter!.Value)));
    }
    private void RequireCategory(InstrumentId id, params ConfiguredDeviceCategory[] categories)
    {
        var device = ImportDevices.SingleOrDefault(d => d.Descriptor.Id == id);
        if (device is null || !categories.Contains(device.Category)) throw new ArgumentException($"{id}: choose a registered device with the required execution role.");
    }
    public bool SupportsStructureEditing => documents is IExperimentPlanStructureDocument;
    public IReadOnlyList<DeviceDescriptor> Devices => runs.DeviceDescriptors;
    public IReadOnlyList<AxisControllerDefinition> AxisDefinitions => runs.AxisDefinitions;
    public IReadOnlyList<AxisControllerDefinition> TemperatureScanDefinitions => runs.TemperatureScanDefinitions;
    public IReadOnlyList<MatrixSwitchDefinition> MatrixDefinitions => runs.MatrixDefinitions;
    public IReadOnlyList<MatrixSwitchDefinition> Sr830AcIsolationDefinitions => runs.Sr830AcIsolationDefinitions;
    public ValidatedPlanDocument UpdateSourcePreparation(ExperimentPlan plan, SourcePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation); runs.PreviewPlan(plan);
        var items = plan.Preparations.ToList(); var index = items.FindIndex(p => p.Device == preparation.Device);
        if (index < 0) items.Add(preparation); else items[index] = preparation;
        return Inspect(new(plan.Name, plan.Root, items, plan.Metadata, plan.Routing, plan.CurrentSourcePreparations, plan.MaximumStepExecutions));
    }
    public ValidatedPlanDocument UpdateCurrentSourcePreparation(ExperimentPlan plan, CurrentSourcePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation); runs.PreviewPlan(plan);
        var items = plan.CurrentSourcePreparations.ToList(); var index = items.FindIndex(p => p.Device == preparation.Device);
        if (index < 0) items.Add(preparation); else items[index] = preparation;
        return Inspect(new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, plan.Routing, items, plan.MaximumStepExecutions));
    }
    public ValidatedPlanDocument RemovePreparation(ExperimentPlan plan, InstrumentId device)
    {
        runs.PreviewPlan(plan);
        if (!plan.Preparations.Any(p => p.Device == device) && !plan.CurrentSourcePreparations.Any(p => p.Device == device)) throw new ArgumentException("This device has no plan preparation.", nameof(device));
        return Inspect(new(plan.Name, plan.Root, plan.Preparations.Where(p => p.Device != device), plan.Metadata, plan.Routing,
            plan.CurrentSourcePreparations.Where(p => p.Device != device), plan.MaximumStepExecutions));
    }
    public PlanCalculationDraftPreview DescribeCalculationDraft(ExperimentPlan plan, StepId target, PlanStructureEditKind kind, PlanCalculationDraft draft)
        => runs.DescribeCalculationDraft(plan, target, kind, draft);
    public IReadOnlyList<PlanVariableAvailability> DescribeVariables(ExperimentPlan plan, StepId step, PlanVariablePosition position = PlanVariablePosition.BeforeStep)
        => runs.DescribeVariables(plan, step, position);
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
    public async Task<RoutingProfileCatalog> LoadRoutingProfilesAsync(string channelsPath, string samplesPath, string instrumentPortsPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var profiles = await (routingProfiles ?? throw new InvalidOperationException("No routing profile importer is configured.")).LoadAsync(channelsPath, samplesPath, instrumentPortsPath, token);
        token.ThrowIfCancellationRequested();
        return profiles;
    }
    public ValidatedPlanDocument Import(string text, ExperimentImportBindings bindings)
        => Inspect((importer ?? throw new InvalidOperationException("No experiment importer is configured.")).Parse(text, bindings));
    public async Task<ValidatedPlanDocument> ImportAsync(string path, ExperimentImportBindings bindings, CancellationToken token, long? maximumStepExecutions = null)
    {
        token.ThrowIfCancellationRequested();
        if (maximumStepExecutions is < 1 or > ExperimentPlan.MaximumSupportedStepExecutions) throw new ArgumentOutOfRangeException(nameof(maximumStepExecutions));
        var plan = await (importer ?? throw new InvalidOperationException("No experiment importer is configured.")).LoadAsync(path, bindings, token);
        token.ThrowIfCancellationRequested();
        if (maximumStepExecutions is { } budget)
            plan = new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, plan.Routing, plan.CurrentSourcePreparations, budget);
        return Inspect(plan);
    }
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

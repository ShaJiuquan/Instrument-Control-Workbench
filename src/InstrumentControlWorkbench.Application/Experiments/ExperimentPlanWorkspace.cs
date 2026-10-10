using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record ValidatedPlanDocument(ExperimentPlan Plan, string Text, ExperimentPlanPreview Preview);

public sealed class ExperimentPlanWorkspace(IExperimentPlanDocument documents, MeasurementRunService runs)
{
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

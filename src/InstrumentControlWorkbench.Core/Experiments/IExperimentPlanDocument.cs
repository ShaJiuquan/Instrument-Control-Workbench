namespace InstrumentControlWorkbench.Core.Experiments;

// A real document boundary: codecs/file IO remain outside the domain and UI.
public interface IExperimentPlanDocument
{
    string Serialize(ExperimentPlan plan);
    ExperimentPlan Parse(string text);
    Task<ExperimentPlan> LoadAsync(string path, CancellationToken token);
    Task SaveAsync(string path, ExperimentPlan plan, CancellationToken token);
}

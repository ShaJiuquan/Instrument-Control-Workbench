using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Experiments;

public enum PlanVariablePosition { BeforeStep, AppendChild }

// Static possibilities at a validated program position, not runtime values.
public sealed record PlanVariableAvailability(ParameterId Id, BlackboardValueType Type, bool IsActiveAxis, IReadOnlyList<StepId> PossibleDefinitions);

internal sealed class VariableFlowState : Dictionary<ParameterId, BlackboardValueType>
{
    private readonly Dictionary<ParameterId, IReadOnlyList<StepId>> definitions = new();
    public VariableFlowState() { }
    public VariableFlowState(VariableFlowState source) : base(source)
    { foreach (var entry in source.definitions) definitions[entry.Key] = entry.Value; }
    public void Define(ParameterId id, StepId source) => definitions[id] = Array.AsReadOnly(new[] { source });
    public IReadOnlyList<StepId> Definitions(ParameterId id) => definitions.GetValueOrDefault(id, Array.Empty<StepId>());
    public void SetDefinitions(ParameterId id, IEnumerable<StepId> sources)
        => definitions[id] = Array.AsReadOnly(sources.Distinct().OrderBy(step => step.Value, StringComparer.Ordinal).ToArray());
    public IReadOnlyList<PlanVariableAvailability> Capture(IReadOnlyList<ParameterId> axes) => Array.AsReadOnly(this.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
        .Select(entry => new PlanVariableAvailability(entry.Key, entry.Value, axes.Contains(entry.Key), Definitions(entry.Key))).ToArray());
}

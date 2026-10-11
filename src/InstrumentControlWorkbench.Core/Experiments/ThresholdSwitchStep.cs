using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Ordered lower bounds: last matching threshold wins. The last child is also
// the default, even when it has a threshold of its own. Thresholds form a prefix.
public sealed record ThresholdSwitchStep : ExperimentStep
{
    public ThresholdSwitchStep(StepId id, ParameterId variable, IEnumerable<ExperimentStep> children,
        IEnumerable<Quantity> thresholds) : base(id)
    {
        ArgumentNullException.ThrowIfNull(variable); ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(thresholds);
        var branches = children.ToArray(); var bounds = thresholds.ToArray();
        if (branches.Length > 10_000 || branches.Any(c => c is null) || bounds.Length > branches.Length)
            throw new ArgumentException("Switch needs bounded non-null children and at most one prefix threshold per child.");
        if (bounds.Select(q => q.Unit).Distinct().Count() > 1)
            throw new ArgumentException("Switch thresholds must share physical units.", nameof(thresholds));
        Variable = variable; Children = Array.AsReadOnly(branches); Thresholds = Array.AsReadOnly(bounds);
    }
    public ParameterId Variable { get; }
    public IReadOnlyList<ExperimentStep> Children { get; }
    public IReadOnlyList<Quantity> Thresholds { get; }

    public int? SelectBranch(Quantity value)
    {
        if (Children.Count == 0) return null;
        if (Thresholds.Count != 0 && Thresholds[0].Unit != value.Unit)
            throw new ArgumentException("Switch selector and thresholds must share physical units.", nameof(value));
        var selected = Children.Count - 1;
        for (var index = 0; index < Thresholds.Count; index++)
            if (value.Value >= Thresholds[index].Value) selected = index;
        return selected;
    }
}

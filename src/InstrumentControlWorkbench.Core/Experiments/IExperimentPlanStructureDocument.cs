namespace InstrumentControlWorkbench.Core.Experiments;

// Format-specific fragments remain behind the document adapter. The UI never
// reconstructs execution semantics from display coordinates or arbitrary wires.
public sealed record PlanStructureNode(StepId Id, string Kind, StepId? Parent, string Slot, int Position, int Depth,
    bool CanAppend, bool CanRemove, bool CanMoveEarlier, bool CanMoveLater);
public enum PlanStructureEditKind { Replace, Append, Remove, MoveEarlier, MoveLater }
public sealed record PlanStructureEdit(PlanStructureEditKind Kind, StepId Selected, string? Fragment = null);
public interface IExperimentPlanStructureDocument : IExperimentPlanDocument
{
    IReadOnlyList<PlanStructureNode> DescribeStructure(ExperimentPlan plan);
    string ReadFragment(ExperimentPlan plan, StepId selected);
    ExperimentPlan EditStructure(ExperimentPlan plan, PlanStructureEdit edit);
}

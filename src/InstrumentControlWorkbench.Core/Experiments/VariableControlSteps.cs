using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum VariableMutation { Set, Add, Subtract, Multiply }

public sealed record MutateValueStep : ExperimentStep
{
    public MutateValueStep(StepId id, ParameterId variable, VariableMutation action, Quantity value) : base(id)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (action == VariableMutation.Multiply && value.Unit != PhysicalUnit.Dimensionless) throw new ArgumentException("A multiplication factor must be dimensionless.");
        Variable = variable; Action = action; Value = value;
    }
    public ParameterId Variable { get; }
    public VariableMutation Action { get; }
    public Quantity Value { get; }
}

public sealed record OutcomeMutationStep : ExperimentStep
{
    public OutcomeMutationStep(StepId id, ExperimentStep body, MutateValueStep? success = null, MutateValueStep? failure = null) : base(id)
    {
        ArgumentNullException.ThrowIfNull(body); Body = body; Success = success; Failure = failure;
    }
    public ExperimentStep Body { get; }
    public MutateValueStep? Success { get; }
    public MutateValueStep? Failure { get; }
}

public sealed record WhileStep : ExperimentStep
{
    public WhileStep(StepId id, ValueCondition condition, ExperimentStep body) : base(id)
    {
        ArgumentNullException.ThrowIfNull(condition); ArgumentNullException.ThrowIfNull(body);
        Condition = condition; Body = body;
    }
    public ValueCondition Condition { get; }
    public ExperimentStep Body { get; }
    public ParameterId IterationColumn => new(Id.Value + ".iteration");
}

public sealed class PlanExecutionBudgetException(StepId step, long maximum) : Exception($"Plan execution budget {maximum} exhausted before {step}.")
{
    public StepId Step { get; } = step;
    public long Maximum { get; } = maximum;
}

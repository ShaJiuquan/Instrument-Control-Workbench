using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Expected workflow results are distinct from transport, validation and safety errors.
public enum StepOutcome { Success, Failure }
public enum ResultEffect { ForceSuccess, ForceFailure, Invert }

public sealed record TimeoutStep : ExperimentStep
{
    public TimeoutStep(StepId id, TimeSpan timeout, ExperimentStep body) : base(id)
    {
        ExperimentDurations.Validate(timeout); ArgumentNullException.ThrowIfNull(body);
        Timeout = timeout; Body = body;
    }
    public TimeSpan Timeout { get; }
    public ExperimentStep Body { get; }
}

public sealed class PlanStepTimeoutException : TimeoutException
{
    public PlanStepTimeoutException(StepId step, TimeSpan timeout, Exception? innerException = null)
        : base($"Plan step {step} exceeded its {timeout.TotalSeconds} s wall-clock timeout.", innerException)
    {
        ArgumentNullException.ThrowIfNull(step); ExperimentDurations.Validate(timeout);
        Step = step; Timeout = timeout;
    }
    public StepId Step { get; }
    public TimeSpan Timeout { get; }
}

public sealed record ConditionStep : ExperimentStep
{
    public ConditionStep(StepId id, ValueCondition condition) : base(id)
    {
        ArgumentNullException.ThrowIfNull(condition); ArgumentNullException.ThrowIfNull(condition.Variable);
        ArgumentNullException.ThrowIfNull(condition.Value);
        if (!Enum.IsDefined(condition.Operator)) throw new ArgumentOutOfRangeException(nameof(condition));
        Condition = condition;
    }
    public ValueCondition Condition { get; }
}

public sealed record FallbackStep : ExperimentStep
{
    public FallbackStep(StepId id, IEnumerable<ExperimentStep> children) : base(id)
    {
        ArgumentNullException.ThrowIfNull(children); var items = children.ToArray();
        if (items.Length is < 1 or > 10_000 || items.Any(c => c is null)) throw new ArgumentException("Fallback requires 1..10,000 non-null branches.", nameof(children));
        Children = Array.AsReadOnly(items); BranchColumn = new(id + ".branch"); TotalColumn = new(id + ".total");
    }
    public IReadOnlyList<ExperimentStep> Children { get; }
    public ParameterId BranchColumn { get; }
    public ParameterId TotalColumn { get; }
}

public sealed record RetryStep : ExperimentStep
{
    public RetryStep(StepId id, int maxAttempts, TimeSpan delay, ExperimentStep body) : base(id)
    {
        if (maxAttempts is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        ExperimentDurations.Validate(delay); ArgumentNullException.ThrowIfNull(body);
        MaxAttempts = maxAttempts; Delay = delay; Body = body; AttemptColumn = new(id + ".attempt"); TotalColumn = new(id + ".total");
    }
    public int MaxAttempts { get; }
    public TimeSpan Delay { get; }
    public ExperimentStep Body { get; }
    public ParameterId AttemptColumn { get; }
    public ParameterId TotalColumn { get; }
}

public sealed record ResultDecoratorStep : ExperimentStep
{
    public ResultDecoratorStep(StepId id, ResultEffect effect, ExperimentStep body) : base(id)
    {
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        ArgumentNullException.ThrowIfNull(body); Effect = effect; Body = body;
    }
    public ResultEffect Effect { get; }
    public ExperimentStep Body { get; }
}

public sealed class PlanFailedException(StepId step) : Exception($"Plan step {step} reported a workflow failure.")
{
    public StepId Step { get; } = step ?? throw new ArgumentNullException(nameof(step));
}

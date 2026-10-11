using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Expected workflow results are distinct from transport, validation and safety errors.
public enum StepOutcome { Success, Failure }
public enum ResultEffect { ForceSuccess, ForceFailure, Invert }

public sealed record BranchOnOutcomeStep : ExperimentStep
{
    public BranchOnOutcomeStep(StepId id, ExperimentStep condition, ExperimentStep then, ExperimentStep? otherwise = null) : base(id)
    {
        ArgumentNullException.ThrowIfNull(condition); ArgumentNullException.ThrowIfNull(then);
        Condition = condition; Then = then; Else = otherwise;
    }
    public ExperimentStep Condition { get; }
    public ExperimentStep Then { get; }
    public ExperimentStep? Else { get; }
}

public sealed record VariableBinding
{
    public VariableBinding(ParameterId source, ParameterId destination)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        Source = source; Destination = destination;
    }
    public ParameterId Source { get; }
    public ParameterId Destination { get; }
}

public sealed record SubtreeStep : ExperimentStep
{
    public SubtreeStep(StepId id, IEnumerable<ExperimentStep> children, IEnumerable<VariableBinding>? inputs = null, IEnumerable<VariableBinding>? outputs = null) : base(id)
    {
        ArgumentNullException.ThrowIfNull(children); var items = children.ToArray();
        if (items.Length > 10_000 || items.Any(c => c is null)) throw new ArgumentException("Subtree requires at most 10,000 non-null children.", nameof(children));
        var mappings = new[] { (inputs ?? []).ToArray(), (outputs ?? []).ToArray() };
        foreach (var map in mappings)
            if (map.Length > 10_000 || map.Any(m => m is null) || map.Select(m => m.Source).Distinct().Count() != map.Length || map.Select(m => m.Destination).Distinct().Count() != map.Length)
                throw new ArgumentException("Subtree mappings require at most 10,000 non-null bindings with distinct sources and destinations.");
        Children = Array.AsReadOnly(items); Inputs = Array.AsReadOnly(mappings[0]); Outputs = Array.AsReadOnly(mappings[1]);
    }
    public IReadOnlyList<ExperimentStep> Children { get; }
    public IReadOnlyList<VariableBinding> Inputs { get; }
    public IReadOnlyList<VariableBinding> Outputs { get; }
}

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

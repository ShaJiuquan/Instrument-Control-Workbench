using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record StepId
{
    public StepId(string value) { Value = new ParameterId(value).Value; }
    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record ParameterAddress
{
    public ParameterAddress(InstrumentId device, ParameterId parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        ArgumentNullException.ThrowIfNull(parameter);
        Device = device;
        Parameter = parameter;
    }
    public InstrumentId Device { get; }
    public ParameterId Parameter { get; }
}

public abstract record PlanValue;
public sealed record ConstantValue(Quantity Value) : PlanValue;
public sealed record VariableValue(ParameterId Variable) : PlanValue;
public enum ComparisonOperator { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }
public sealed record ValueCondition(ParameterId Variable, ComparisonOperator Operator, PlanValue Value);
public sealed record ReadChannel(ParameterId Column, ParameterAddress Address);

public abstract record ExperimentStep
{
    protected ExperimentStep(StepId id) { ArgumentNullException.ThrowIfNull(id); Id = id; }
    public StepId Id { get; }
}

public sealed record SequenceStep : ExperimentStep
{
    public SequenceStep(StepId id, IEnumerable<ExperimentStep> children) : base(id)
    {
        ArgumentNullException.ThrowIfNull(children);
        var items = children.ToArray();
        if (items.Any(c => c is null)) throw new ArgumentException("Steps must be non-null.", nameof(children));
        Children = Array.AsReadOnly(items);
    }
    public IReadOnlyList<ExperimentStep> Children { get; }
}

public sealed record RepeatStep : ExperimentStep
{
    public RepeatStep(StepId id, int cycles, ExperimentStep body) : base(id)
    {
        if (cycles is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(cycles));
        ArgumentNullException.ThrowIfNull(body);
        Cycles = cycles; Body = body;
    }
    public int Cycles { get; }
    public ExperimentStep Body { get; }
}

public sealed record ScanStep : ExperimentStep
{
    public ScanStep(StepId id, ParameterAddress axis, ParameterId column, ScanValues values, TimeSpan dwell, ExperimentStep body) : base(id)
    {
        ArgumentNullException.ThrowIfNull(axis); ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(body);
        ExperimentDurations.Validate(dwell);
        Axis = axis; Column = column; Values = values; Dwell = dwell; Body = body;
    }
    public ParameterAddress Axis { get; }
    public ParameterId Column { get; }
    public ScanValues Values { get; }
    public TimeSpan Dwell { get; }
    public ExperimentStep Body { get; }
}

public sealed record ForEachStep : ExperimentStep
{
    public ForEachStep(StepId id, ParameterId variable, ScanValues values, ExperimentStep body) : base(id)
    {
        ArgumentNullException.ThrowIfNull(variable); ArgumentNullException.ThrowIfNull(values); ArgumentNullException.ThrowIfNull(body);
        Variable = variable; Values = values; Body = body;
    }
    public ParameterId Variable { get; }
    public ScanValues Values { get; }
    public ExperimentStep Body { get; }
}

public sealed record SetParameterStep : ExperimentStep
{
    public SetParameterStep(StepId id, ParameterAddress address, PlanValue value) : base(id)
    {
        ArgumentNullException.ThrowIfNull(address); ArgumentNullException.ThrowIfNull(value);
        Address = address; Value = value;
    }
    public ParameterAddress Address { get; }
    public PlanValue Value { get; }
}
public sealed record AssignValueStep : ExperimentStep
{
    public AssignValueStep(StepId id, ParameterId variable, PlanValue value) : base(id)
    {
        ArgumentNullException.ThrowIfNull(variable); ArgumentNullException.ThrowIfNull(value);
        Variable = variable; Value = value;
    }
    public ParameterId Variable { get; }
    public PlanValue Value { get; }
}
public sealed record SourceOutputStep : ExperimentStep
{
    public SourceOutputStep(StepId id, InstrumentId device, bool enabled) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); Device = device; Enabled = enabled;
    }
    public InstrumentId Device { get; }
    public bool Enabled { get; }
}
public sealed record IfElseStep : ExperimentStep
{
    public IfElseStep(StepId id, ValueCondition condition, ExperimentStep then, ExperimentStep? otherwise = null) : base(id)
    {
        ArgumentNullException.ThrowIfNull(condition); ArgumentNullException.ThrowIfNull(then);
        Condition = condition; Then = then; Else = otherwise;
    }
    public ValueCondition Condition { get; }
    public ExperimentStep Then { get; }
    public ExperimentStep? Else { get; }
}

public sealed record DelayStep : ExperimentStep
{
    public DelayStep(StepId id, TimeSpan duration) : base(id) { ExperimentDurations.Validate(duration); Duration = duration; }
    public TimeSpan Duration { get; }
}

public sealed record ReadStep : ExperimentStep
{
    public ReadStep(StepId id, IEnumerable<ReadChannel> channels, IEnumerable<EnvironmentRead>? environmentReads = null) : base(id)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var items = channels.ToArray();
        if (items.Length == 0 || items.Any(c => c is null) || items.Select(c => c.Column).Distinct().Count() != items.Length)
            throw new ArgumentException("A measurement needs non-null channels with unique column IDs.", nameof(channels));
        Channels = Array.AsReadOnly(items);
        EnvironmentReads = EnvironmentRead.Freeze(environmentReads, items.Select(c => c.Column));
    }
    public IReadOnlyList<ReadChannel> Channels { get; }
    public IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
}

public sealed record WaitStableStep : ExperimentStep
{
    public WaitStableStep(StepId id, ParameterAddress readback, PlanValue target, Quantity tolerance,
        TimeSpan stableFor, TimeSpan pollInterval, TimeSpan timeout) : base(id)
    {
        ArgumentNullException.ThrowIfNull(readback); ArgumentNullException.ThrowIfNull(target);
        if (tolerance.Value < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        ExperimentDurations.Validate(stableFor); ExperimentDurations.Validate(pollInterval, positive: true); ExperimentDurations.Validate(timeout, positive: true);
        if (stableFor > timeout) throw new ArgumentException("Stability duration cannot exceed timeout.");
        Readback = readback; Target = target; Tolerance = tolerance; StableFor = stableFor; PollInterval = pollInterval; Timeout = timeout;
    }
    public ParameterAddress Readback { get; }
    public PlanValue Target { get; }
    public Quantity Tolerance { get; }
    public TimeSpan StableFor { get; }
    public TimeSpan PollInterval { get; }
    public TimeSpan Timeout { get; }
}

public sealed record SourcePreparation
{
    public SourcePreparation(InstrumentId device, SourceFunction function, Quantity level, Quantity compliance, SenseConfiguration sense)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value);
        if (!Enum.IsDefined(function)) throw new ArgumentOutOfRangeException(nameof(function));
        ArgumentNullException.ThrowIfNull(sense);
        if (level.Unit != (function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere) ||
            compliance.Unit != (function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt))
            throw new ArgumentException("Source level and compliance must match the selected function.");
        Keithley2450Limits.Validate(sense);
        Device = device; Function = function; Level = level; Compliance = compliance; Sense = sense;
    }
    public InstrumentId Device { get; }
    public SourceFunction Function { get; }
    public Quantity Level { get; }
    public Quantity Compliance { get; }
    public SenseConfiguration Sense { get; }
    public SourceSetpoint ToSetpoint() => new(Function, Level.Value, Compliance.Value);
}

public sealed record ExperimentPlan
{
    public ExperimentPlan(string name, ExperimentStep root, IEnumerable<SourcePreparation>? preparations = null,
        IReadOnlyDictionary<string, Quantity>? metadata = null, RoutingConfiguration? routing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(root);
        var items = preparations?.ToArray() ?? [];
        if (items.Any(p => p is null) || items.Select(p => p.Device).Distinct().Count() != items.Length)
            throw new ArgumentException("Source preparations must have unique device IDs.", nameof(preparations));
        Name = name; Root = root; Preparations = Array.AsReadOnly(items); Routing = routing;
        Metadata = new ReadOnlyDictionary<string, Quantity>(new Dictionary<string, Quantity>(metadata ?? new Dictionary<string, Quantity>()));
    }
    public const int FormatVersion = 1;
    public string Name { get; }
    public ExperimentStep Root { get; }
    public IReadOnlyList<SourcePreparation> Preparations { get; }
    public RoutingConfiguration? Routing { get; }
    public IReadOnlyDictionary<string, Quantity> Metadata { get; }
}

internal static class ExperimentDurations
{
    public static void Validate(TimeSpan duration, bool positive = false)
    {
        if (duration < TimeSpan.Zero || positive && duration == TimeSpan.Zero || duration > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration is outside the supported timer range.");
    }
}

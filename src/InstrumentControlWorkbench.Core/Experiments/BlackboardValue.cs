using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum BlackboardValueKind { Quantity, Boolean, Text }

public readonly record struct BlackboardValueType
{
    private BlackboardValueType(BlackboardValueKind kind, PhysicalUnit? unit) { Kind = kind; Unit = unit; }
    public BlackboardValueKind Kind { get; }
    public PhysicalUnit? Unit { get; }
    public static BlackboardValueType Quantity(PhysicalUnit unit)
    {
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        return new(BlackboardValueKind.Quantity, unit);
    }
    public static BlackboardValueType Boolean => new(BlackboardValueKind.Boolean, null);
    public static BlackboardValueType Text => new(BlackboardValueKind.Text, null);
}

public abstract record BlackboardValue
{
    private protected BlackboardValue() { }
    public abstract BlackboardValueType Type { get; }
}
public sealed record BlackboardQuantity(Quantity Value) : BlackboardValue
{
    public override BlackboardValueType Type => BlackboardValueType.Quantity(Value.Unit);
}
public sealed record BlackboardBoolean(bool Value) : BlackboardValue
{
    public override BlackboardValueType Type => BlackboardValueType.Boolean;
}
public sealed record BlackboardText : BlackboardValue
{
    public const int MaximumLength = 65_536;
    public BlackboardText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumLength) throw new ArgumentOutOfRangeException(nameof(value), "Blackboard text exceeds 65,536 characters.");
        Value = value;
    }
    public string Value { get; }
    public override BlackboardValueType Type => BlackboardValueType.Text;
}

public sealed record BooleanValue(bool Value) : PlanValue;
public sealed record TextValue : PlanValue
{
    public TextValue(string value) { Value = new BlackboardText(value).Value; }
    public string Value { get; }
}

public enum BlackboardWriteKind { Step, SubtreeInput, SubtreeOutput }
public sealed record BlackboardEntry
{
    public BlackboardEntry(BlackboardValue value, StepId source, DateTimeOffset updatedAt, TimeSpan elapsed, BlackboardWriteKind kind = BlackboardWriteKind.Step)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(source);
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Value = value; Source = source; UpdatedAt = updatedAt; Elapsed = elapsed; Kind = kind;
    }
    public BlackboardValue Value { get; }
    public StepId Source { get; }
    public DateTimeOffset UpdatedAt { get; }
    public TimeSpan Elapsed { get; }
    public BlackboardWriteKind Kind { get; }
}

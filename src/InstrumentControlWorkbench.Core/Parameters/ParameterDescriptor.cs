namespace InstrumentControlWorkbench.Core.Parameters;

public sealed record ParameterId
{
    public ParameterId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Parameter IDs contain letters, digits, '.', '_' or '-'.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

[Flags]
public enum ParameterAccess { Read = 1, Write = 2, ReadWrite = Read | Write }

public sealed record ParameterDescriptor
{
    public ParameterDescriptor(ParameterId id, string label, PhysicalUnit unit, ParameterAccess access,
        Quantity? minimum = null, Quantity? maximum = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        if (!Enum.IsDefined(access)) throw new ArgumentOutOfRangeException(nameof(access));
        if (minimum is { } min && min.Unit != unit || maximum is { } max && max.Unit != unit)
            throw new ArgumentException("Parameter bounds must use the parameter unit.");
        if (minimum is { } lower && maximum is { } upper && lower.Value > upper.Value)
            throw new ArgumentException("Minimum must not exceed maximum.");
        Id = id;
        Label = label;
        Unit = unit;
        Access = access;
        Minimum = minimum;
        Maximum = maximum;
    }

    public ParameterId Id { get; }
    public string Label { get; }
    public PhysicalUnit Unit { get; }
    public ParameterAccess Access { get; }
    public Quantity? Minimum { get; }
    public Quantity? Maximum { get; }

    public void Validate(Quantity value)
    {
        if (value.Unit != Unit) throw new ArgumentException($"{Id} requires {Unit}, received {value.Unit}.", nameof(value));
        if (Minimum is { } min && value.Value < min.Value || Maximum is { } max && value.Value > max.Value)
            throw new ArgumentOutOfRangeException(nameof(value), $"{Id} is outside its allowed range.");
    }

    public void ValidateWrite(Quantity value)
    {
        if (!Access.HasFlag(ParameterAccess.Write)) throw new InvalidOperationException($"{Id} is read-only.");
        Validate(value);
    }
}

namespace InstrumentControlWorkbench.Core.Parameters;

public enum PhysicalUnit
{
    Dimensionless,
    Volt,
    Ampere,
    Ohm,
    Kelvin,
    Tesla,
    Degree,
    Second,
    Hertz,
    Coulomb,
    Pascal,
    KelvinPerMinute,
    TeslaPerSecond,
    DegreePerSecond,
    Meter,
    OhmPerSquare,
    OhmMeter,
}

public readonly record struct Quantity
{
    public Quantity(double value, PhysicalUnit unit)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A quantity must be finite.");
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        Value = value;
        Unit = unit;
    }

    public double Value { get; }
    public PhysicalUnit Unit { get; }
}

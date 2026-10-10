using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// Nominal full-scale ranges, distinct from the 105% maximum source level.
public static class Keithley2450SourceRanges
{
    private static readonly IReadOnlyList<Quantity> voltage = Array.AsReadOnly(new[] { 0.02, 0.2, 2, 20, 200 }.Select(v => new Quantity(v, PhysicalUnit.Volt)).ToArray());
    private static readonly IReadOnlyList<Quantity> current = Array.AsReadOnly(new[] { 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 0.1, 1 }.Select(v => new Quantity(v, PhysicalUnit.Ampere)).ToArray());
    public static IReadOnlyList<Quantity> For(SourceFunction function) => function switch
    {
        SourceFunction.Voltage => voltage,
        SourceFunction.Current => current,
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };
    public static void Validate(SourceFunction function, Quantity range)
    {
        if (!For(function).Contains(range)) throw new ArgumentOutOfRangeException(nameof(range), "Manual source range must be a supported nominal full-scale value with matching units.");
    }
    public static void ValidateLevel(SourceFunction function, double level, Quantity range)
    {
        Validate(function, range);
        if (!double.IsFinite(level) || Math.Abs(level) > range.Value * 1.05)
            throw new ArgumentOutOfRangeException(nameof(level), "Source level exceeds the manual range's 105% envelope.");
    }
    // PPMS accepts approximate positive range selectors; retain a canonical
    // nominal choice for deterministic native plans and actual readback checks.
    public static Quantity Resolve(SourceFunction function, Quantity selector)
    {
        var choices = For(function);
        if (selector.Unit != choices[0].Unit || selector.Value <= 0) throw new ArgumentOutOfRangeException(nameof(selector));
        foreach (var choice in choices) if (selector.Value <= choice.Value) return choice;
        throw new ArgumentOutOfRangeException(nameof(selector), "Source range selector exceeds the instrument profile.");
    }
}

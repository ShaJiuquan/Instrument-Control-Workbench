using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// Nominal measurement spans are distinct from source range and source level.
public static class Keithley2450SenseRanges
{
    private static readonly IReadOnlyList<Quantity> resistance = Array.AsReadOnly(new[] { 20d, 200, 2000, 20000, 200000, 2000000, 20000000, 200000000 }.Select(v => new Quantity(v, PhysicalUnit.Ohm)).ToArray());
    public static IReadOnlyList<Quantity> For(SenseFunction function) => function switch
    {
        SenseFunction.Voltage => Keithley2450SourceRanges.For(SourceFunction.Voltage),
        SenseFunction.Current => Keithley2450SourceRanges.For(SourceFunction.Current),
        SenseFunction.Resistance => resistance,
        _ => throw new ArgumentOutOfRangeException(nameof(function)),
    };
    public static void Validate(SenseFunction function, Quantity range)
    {
        if (!For(function).Contains(range)) throw new ArgumentOutOfRangeException(nameof(range), "Manual sense range must be a supported nominal span with matching units.");
    }
    public static Quantity Resolve(SenseFunction function, Quantity selector)
    {
        var choices = For(function);
        if (selector.Unit != choices[0].Unit || selector.Value <= 0) throw new ArgumentOutOfRangeException(nameof(selector));
        foreach (var choice in choices) if (selector.Value <= choice.Value) return choice;
        throw new ArgumentOutOfRangeException(nameof(selector), "Sense range selector exceeds the instrument profile.");
    }
}

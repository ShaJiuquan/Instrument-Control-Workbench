using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record SoftwareTemperatureRampStep : ExperimentStep
{
    public SoftwareTemperatureRampStep(StepId id, InstrumentId device, PlanValue target, Quantity? rate = null, bool enableControl = true) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(target);
        if (rate is { } r) TemperatureRampPath.ValidateRate(r);
        if (target is ConstantValue literal) TemperatureRampPath.ValidateTemperature(literal.Value);
        Device = device; Target = target; Rate = rate; EnableControl = enableControl;
    }
    public InstrumentId Device { get; }
    public PlanValue Target { get; }
    // Null means an explicitly direct, unrounded set with hardware ramp off.
    public Quantity? Rate { get; }
    // False preserves the observed control flag; it does not turn control off.
    public bool EnableControl { get; }
}

public sealed record TemperatureRampPath
{
    public TemperatureRampPath(Quantity start, Quantity target, Quantity rate)
    {
        ValidateTemperature(start); ValidateTemperature(target); ValidateRate(rate);
        StartMilliKelvin = MilliKelvin(start); TargetMilliKelvin = MilliKelvin(target);
        StepMilliKelvin = Math.Max(1, (long)Math.Round(rate.Value / 60 * 1000, MidpointRounding.AwayFromZero));
        UpdateCount = (Math.Abs(TargetMilliKelvin - StartMilliKelvin) + StepMilliKelvin - 1) / StepMilliKelvin;
    }
    public const int TickSeconds = 1;
    public long StartMilliKelvin { get; }
    public long TargetMilliKelvin { get; }
    public long StepMilliKelvin { get; }
    public long UpdateCount { get; }
    public Quantity RoundedTarget => new(TargetMilliKelvin / 1000.0, PhysicalUnit.Kelvin);
    public IEnumerable<Quantity> Targets()
    {
        var direction = Math.Sign(TargetMilliKelvin - StartMilliKelvin);
        for (long i = 1; i <= UpdateCount; i++)
        {
            var next = StartMilliKelvin + direction * Math.Min(i * StepMilliKelvin, Math.Abs(TargetMilliKelvin - StartMilliKelvin));
            yield return new(next / 1000.0, PhysicalUnit.Kelvin);
        }
    }
    public static long MilliKelvin(Quantity value)
    {
        ValidateTemperature(value); return (long)Math.Round(value.Value * 1000, MidpointRounding.AwayFromZero);
    }
    public static void ValidateTemperature(Quantity value)
    {
        if (value.Unit != PhysicalUnit.Kelvin || value.Value is < 1 or > 310) throw new ArgumentOutOfRangeException(nameof(value), "Software temperature targets/readbacks require 1..310 K.");
    }
    public static void ValidateRate(Quantity value)
    {
        if (value.Unit != PhysicalUnit.KelvinPerMinute || value.Value is < 0.01 or > 20) throw new ArgumentOutOfRangeException(nameof(value), "Software temperature rate requires 0.01..20 K/min.");
    }
}

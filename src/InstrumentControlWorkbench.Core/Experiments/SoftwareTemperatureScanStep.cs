using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record SoftwareTemperatureScanStep : TemperatureScanStep
{
    public SoftwareTemperatureScanStep(StepId id, InstrumentId device, ParameterId column, ScanValues points, Quantity rate,
        bool waitStableFirst, Quantity tolerance, int stableCount, TimeSpan stableTimeout, TimeSpan stepTimeout,
        TimeSpan pollInterval, TemperatureRampSampling sampling, TimeSpan interval, ExperimentStep body)
        : base(id, device, column, points, rate, waitStableFirst, tolerance, stableCount, stableTimeout, stepTimeout, pollInterval, sampling, interval, body) { }
    public ParameterId SetpointColumn => new(Column.Value + ".setpoint");
}

// Pure elapsed-time trajectory used by the software scan; no one-second tick floor.
public sealed record SoftwareTemperatureTrajectory
{
    public SoftwareTemperatureTrajectory(Quantity first, Quantity last, Quantity rate)
    {
        TemperatureRampPath.ValidateTemperature(first); TemperatureRampPath.ValidateTemperature(last); TemperatureRampPath.ValidateRate(rate);
        if (first == last) throw new ArgumentException("Trajectory needs a nonzero temperature span.");
        First = first; Last = last; Rate = rate;
    }
    public Quantity First { get; }
    public Quantity Last { get; }
    public Quantity Rate { get; }
    public Quantity RoundedLast => Round(Last);
    public Quantity At(TimeSpan activeElapsed)
    {
        if (activeElapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activeElapsed));
        var delta = Rate.Value / 60 * activeElapsed.TotalSeconds;
        return Round(new(Last.Value > First.Value ? Math.Min(Last.Value, First.Value + delta) : Math.Max(Last.Value, First.Value - delta), PhysicalUnit.Kelvin));
    }
    public static Quantity Round(Quantity value)
    {
        TemperatureRampPath.ValidateTemperature(value); return new((double)decimal.Round((decimal)value.Value, 3, MidpointRounding.AwayFromZero), PhysicalUnit.Kelvin);
    }
}

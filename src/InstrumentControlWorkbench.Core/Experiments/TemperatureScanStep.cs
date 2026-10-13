using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum TemperatureRampSampling { Threshold, EstimatedSetpoint, Timed, Immediate }

public abstract record TemperatureScanStep : ExperimentStep
{
    protected TemperatureScanStep(StepId id, InstrumentId device, ParameterId column, ScanValues points, Quantity rate,
        bool waitStableFirst, Quantity tolerance, int stableCount, TimeSpan stableTimeout, TimeSpan stepTimeout,
        TimeSpan pollInterval, TemperatureRampSampling sampling, TimeSpan interval, ExperimentStep body) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(column); ArgumentNullException.ThrowIfNull(points); ArgumentNullException.ThrowIfNull(body);
        if (points.Unit != PhysicalUnit.Kelvin || points.Values.Count < 2 || !Enum.IsDefined(sampling)) throw new ArgumentException("Temperature ramp needs >=2 Kelvin points and a defined sampling mode.");
        foreach (var point in points.Values) TemperatureRampPath.ValidateTemperature(point); TemperatureRampPath.ValidateRate(rate);
        var direction = Math.Sign(points.Values[^1].Value - points.Values[0].Value);
        if (direction == 0 || points.Values.Zip(points.Values.Skip(1)).Any(pair => (pair.Second.Value - pair.First.Value) * direction < 0)) throw new ArgumentException("Continuous ramp points require one nonzero direction.");
        if (tolerance.Unit != PhysicalUnit.Kelvin || tolerance.Value <= 0 || stableCount is < 1 or > 10000) throw new ArgumentException("Ramp needs positive Kelvin tolerance and 1..10000 consecutive stable observations.");
        foreach (var time in new[] { stableTimeout, stepTimeout, pollInterval, interval }) ExperimentDurations.Validate(time);
        if (stableTimeout <= TimeSpan.Zero || stepTimeout <= TimeSpan.Zero || pollInterval < TimeSpan.FromMilliseconds(50) || interval < TimeSpan.FromMilliseconds(1)) throw new ArgumentException("Ramp requires positive deadlines, >=50 ms polling and >=1 ms interval.");
        Device = device; Column = column; Points = points; Rate = rate; WaitStableFirst = waitStableFirst; Tolerance = tolerance; StableCount = stableCount; StableTimeout = stableTimeout; StepTimeout = stepTimeout; PollInterval = pollInterval; Sampling = sampling; Interval = interval; Body = body;
    }
    public InstrumentId Device { get; }
    public ParameterId Column { get; }
    public ParameterId IndexColumn => new(Column.Value + ".index");
    public ParameterId TotalColumn => new(Column.Value + ".total");
    public ScanValues Points { get; }
    public Quantity Rate { get; }
    public bool WaitStableFirst { get; }
    public Quantity Tolerance { get; }
    public int StableCount { get; }
    public TimeSpan StableTimeout { get; }
    public TimeSpan StepTimeout { get; }
    public TimeSpan PollInterval { get; }
    public TemperatureRampSampling Sampling { get; }
    public TimeSpan Interval { get; }
    public ExperimentStep Body { get; }
    public TimeSpan NominalDuration => TimeSpan.FromSeconds(Math.Abs(Points.Values[^1].Value - Points.Values[0].Value) / Rate.Value * 60);
    public long ProgressTotal => Sampling == TemperatureRampSampling.Immediate ? 0 : Sampling == TemperatureRampSampling.Timed ? checked((long)Math.Ceiling(NominalDuration.TotalSeconds / Interval.TotalSeconds) + 1) : Points.Values.Count;
}

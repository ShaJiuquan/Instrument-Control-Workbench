using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum CoolingSamplingMode { Threshold, Timed, Immediate }

public sealed record TemperatureCoolingStep : ExperimentStep
{
    public TemperatureCoolingStep(StepId id, InstrumentId device, ParameterId column, ScanValues thresholds, Quantity coolingTarget,
        Quantity endTemperature, Quantity tolerance, TimeSpan timeout, TimeSpan pollInterval, CoolingSamplingMode sampling,
        TimeSpan interval, ExperimentStep body) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(column); ArgumentNullException.ThrowIfNull(thresholds); ArgumentNullException.ThrowIfNull(body);
        if (!Enum.IsDefined(sampling) || thresholds.Unit != PhysicalUnit.Kelvin) throw new ArgumentException("Cooling requires Kelvin thresholds and a defined sampling mode.");
        foreach (var point in thresholds.Values) TemperatureRampPath.ValidateTemperature(point);
        TemperatureRampPath.ValidateTemperature(coolingTarget); TemperatureRampPath.ValidateTemperature(endTemperature);
        if (thresholds.Values.Zip(thresholds.Values.Skip(1)).Any(pair => pair.Second.Value > pair.First.Value) || coolingTarget.Value > endTemperature.Value || endTemperature.Value > thresholds.Values[^1].Value)
            throw new ArgumentException("Cooling thresholds must descend; target <= end <= final threshold.");
        if (tolerance.Unit != PhysicalUnit.Kelvin || tolerance.Value < 0) throw new ArgumentException("Cooling tolerance must be nonnegative Kelvin.");
        ExperimentDurations.Validate(timeout); ExperimentDurations.Validate(pollInterval); ExperimentDurations.Validate(interval);
        if (timeout <= TimeSpan.Zero || pollInterval < TimeSpan.FromMilliseconds(50) || interval < TimeSpan.FromMilliseconds(1)) throw new ArgumentException("Cooling requires positive timeout, >=50 ms polling and >=1 ms interval.");
        Device = device; Column = column; Thresholds = endTemperature.Value < thresholds.Values[^1].Value ? new ScanValues([.. thresholds.Values, endTemperature]) : thresholds; CoolingTarget = coolingTarget; EndTemperature = endTemperature; Tolerance = tolerance; Timeout = timeout; PollInterval = pollInterval; Sampling = sampling; Interval = interval; Body = body;
    }
    public InstrumentId Device { get; }
    public ParameterId Column { get; }
    public ParameterId IndexColumn => new(Column.Value + ".index");
    public ParameterId TotalColumn => new(Column.Value + ".total");
    public ScanValues Thresholds { get; }
    public Quantity CoolingTarget { get; }
    public Quantity EndTemperature { get; }
    public Quantity Tolerance { get; }
    public TimeSpan Timeout { get; }
    public TimeSpan PollInterval { get; }
    public CoolingSamplingMode Sampling { get; }
    public TimeSpan Interval { get; }
    public ExperimentStep Body { get; }
}

using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record HardwareTemperatureScanStep : TemperatureScanStep
{
    public HardwareTemperatureScanStep(StepId id, InstrumentId device, ParameterId column, ScanValues points, Quantity rate,
        bool waitStableFirst, Quantity tolerance, int stableCount, TimeSpan stableTimeout, TimeSpan stepTimeout,
        TimeSpan pollInterval, TemperatureRampSampling sampling, TimeSpan interval, ExperimentStep body)
        : base(id, device, column, points, rate, waitStableFirst, tolerance, stableCount, stableTimeout, stepTimeout, pollInterval, sampling, interval, body) { }
}

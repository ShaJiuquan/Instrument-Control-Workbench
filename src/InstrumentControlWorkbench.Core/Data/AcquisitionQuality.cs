using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public enum ReadingQualityState { Unobserved, PartiallyObserved, ObservedNormal, ObservedWithIssues, ContainsFailedAcquisitions }

// One result-column's original acquisition evidence. Analysis and wide rows
// preserve these input references; they do not invent new hardware status.
public sealed record AcquisitionQuality
{
    public AcquisitionQuality(StepId step, InstrumentId device, ParameterId column, DateTimeOffset capturedAt, Keithley2450ReadingStatus? status, Keithley2450AcquisitionSettings? acquisitionConfiguration = null, Keithley2182Settings? meterConfiguration = null, Keithley6485Settings? picoammeterConfiguration = null, Keithley6514Settings? electrometerConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(step); ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(column);
        if (meterConfiguration is not null && (status is not null || acquisitionConfiguration is not null)) throw new ArgumentException("Passive meter evidence cannot claim K2450 buffer status or settings.");
        if (picoammeterConfiguration is not null && (status is not null || acquisitionConfiguration is not null || meterConfiguration is not null || picoammeterConfiguration.ZeroCheck)) throw new ArgumentException("Picoammeter evidence requires zero check off and cannot claim other instrument status or settings.");
        if (electrometerConfiguration is not null && (status is not null || acquisitionConfiguration is not null || meterConfiguration is not null || picoammeterConfiguration is not null || electrometerConfiguration.ZeroCheck)) throw new ArgumentException("Electrometer evidence requires zero check off and cannot claim other instrument status or settings.");
        ElectrometerConfiguration = electrometerConfiguration;
        PicoammeterConfiguration = picoammeterConfiguration;
        Step = step; Device = device; Column = column; CapturedAt = capturedAt; Status = status; AcquisitionConfiguration = acquisitionConfiguration; MeterConfiguration = meterConfiguration;
    }
    public Keithley6514Settings? ElectrometerConfiguration { get; }
    public Keithley6485Settings? PicoammeterConfiguration { get; }
    public Keithley2182Settings? MeterConfiguration { get; }
    public StepId Step { get; }
    public InstrumentId Device { get; }
    public ParameterId Column { get; }
    public DateTimeOffset CapturedAt { get; }
    public Keithley2450ReadingStatus? Status { get; }
    public Keithley2450AcquisitionSettings? AcquisitionConfiguration { get; }
}

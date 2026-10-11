using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public enum ReadingQualityState { Unobserved, PartiallyObserved, ObservedNormal, ObservedWithIssues }

// One result-column's original acquisition evidence. Analysis and wide rows
// preserve these input references; they do not invent new hardware status.
public sealed record AcquisitionQuality
{
    public AcquisitionQuality(StepId step, InstrumentId device, ParameterId column, DateTimeOffset capturedAt, Keithley2450ReadingStatus? status)
    {
        ArgumentNullException.ThrowIfNull(step); ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(column);
        Step = step; Device = device; Column = column; CapturedAt = capturedAt; Status = status;
    }
    public StepId Step { get; }
    public InstrumentId Device { get; }
    public ParameterId Column { get; }
    public DateTimeOffset CapturedAt { get; }
    public Keithley2450ReadingStatus? Status { get; }
}

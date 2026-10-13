using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Parameters;

public sealed record ParameterObservation(Quantity Value, DateTimeOffset CapturedAt, Keithley2450ReadingStatus? Status = null, Keithley2450AcquisitionSettings? AcquisitionConfiguration = null, Keithley2182Settings? MeterConfiguration = null, Keithley6485Settings? PicoammeterConfiguration = null);

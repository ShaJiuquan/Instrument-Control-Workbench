namespace InstrumentControlWorkbench.Core.Instruments;

// A fresh adapter observation, never inferred from cached requested mode or
// output-off alone. Null facts remain unknown and cannot admit DC operation.
public sealed record Keithley6221ModeObservation
{
    public Keithley6221ModeObservation(bool? deltaArmed, bool? pulseDeltaArmed, bool? differentialArmed, bool? waveformArmed, bool? sweepArmed,
        ushort? operationCondition, DateTimeOffset capturedAt)
    {
        DeltaArmed = deltaArmed; PulseDeltaArmed = pulseDeltaArmed; DifferentialArmed = differentialArmed; WaveformArmed = waveformArmed;
        SweepArmed = sweepArmed;
        OperationCondition = operationCondition; CapturedAt = capturedAt;
    }
    public bool? DeltaArmed { get; }
    public bool? PulseDeltaArmed { get; }
    public bool? DifferentialArmed { get; }
    public bool? WaveformArmed { get; }
    public bool? SweepArmed { get; }
    public ushort? OperationCondition { get; }
    public DateTimeOffset CapturedAt { get; }
    // Reference manual section11: calibration/sweeping/wave-start/trigger wait/
    // arm wait/RS232 error disqualify DC. Require Idle and reject unknown bits.
    private const ushort KnownBits = 0x0DFF;
    private const ushort UnsafeBits = 0x0879;
    public bool IsDcReady => DeltaArmed == false && PulseDeltaArmed == false && DifferentialArmed == false && WaveformArmed == false && SweepArmed == false &&
        OperationCondition is { } condition && (condition & 0x0400) != 0 && (condition & UnsafeBits) == 0 && (condition & ~KnownBits) == 0;
}

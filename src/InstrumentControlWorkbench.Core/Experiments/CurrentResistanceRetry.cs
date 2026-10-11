namespace InstrumentControlWorkbench.Core.Experiments;

// Null policy stops the run on any failure. This policy retries only a typed,
// terminal K2182 voltage-read failure after fresh bounded idle confirmation.
// Explicit continuation can instead record eligible terminal failures as invalid points.
public sealed record CurrentResistanceRetry
{
    public CurrentResistanceRetry(bool ownsSourceOutput, TimeSpan settle, bool continueOnAcquisitionFailure = false)
    {
        ExperimentDurations.Validate(settle);
        if (!ownsSourceOutput && settle != TimeSpan.Zero) throw new ArgumentException("Managed excitation retries do not own source settling.");
        ContinueOnAcquisitionFailure = continueOnAcquisitionFailure; OwnsSourceOutput = ownsSourceOutput; Settle = settle;
    }
    public bool ContinueOnAcquisitionFailure { get; }
    public bool OwnsSourceOutput { get; }
    public TimeSpan Settle { get; }
}

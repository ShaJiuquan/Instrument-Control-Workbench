namespace InstrumentControlWorkbench.Core.Experiments;

// Null policy stops the run on any failure. This policy retries only a typed,
// terminal K2182 voltage-read failure after fresh bounded idle confirmation.
public sealed record CurrentResistanceRetry
{
    public CurrentResistanceRetry(bool ownsSourceOutput, TimeSpan settle)
    {
        ExperimentDurations.Validate(settle);
        if (!ownsSourceOutput && settle != TimeSpan.Zero) throw new ArgumentException("Managed excitation retries do not own source settling.");
        OwnsSourceOutput = ownsSourceOutput; Settle = settle;
    }
    public bool OwnsSourceOutput { get; }
    public TimeSpan Settle { get; }
}

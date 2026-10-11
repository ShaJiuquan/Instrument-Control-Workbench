namespace InstrumentControlWorkbench.Core.Instruments;

[Flags]
public enum ReadingQualityIssues
{
    None = 0,
    Questionable = 1,
    SourceLimited = 2,
    Protection = 4,
    OverTemperature = 8,
    MeasurementLimit = 16,
    UnexpectedOrigin = 32,
    UnknownStatusBits = 64,
}

// Raw buffer words accompany one acquisition. Configuration queried later is
// not evidence of the state (or autorange span) used by that acquisition.
public sealed record Keithley2450ReadingStatus(ushort Source, ushort Measurement)
{
    public bool SourceReadback => (Source & 8) != 0;
    public bool OutputEnabled => (Source & 128) != 0;
    public SensingMode SensingMode => (Source & 64) != 0 ? SensingMode.FourWire : SensingMode.TwoWire;
    public Terminals Terminals => (Measurement & 8) != 0 ? Terminals.Front : Terminals.Rear;
    public ReadingQualityIssues Issues =>
        ((Measurement & 1) != 0 ? ReadingQualityIssues.Questionable : 0) |
        ((Source & 32) != 0 ? ReadingQualityIssues.SourceLimited : 0) |
        ((Source & 4) != 0 ? ReadingQualityIssues.Protection : 0) |
        ((Source & 16) != 0 ? ReadingQualityIssues.OverTemperature : 0) |
        ((Measurement & 240) != 0 ? ReadingQualityIssues.MeasurementLimit : 0) |
        ((Measurement & 6) != 0 ? ReadingQualityIssues.UnexpectedOrigin : 0) |
        ((Source & ~252) != 0 || (Measurement & ~511) != 0 ? ReadingQualityIssues.UnknownStatusBits : 0);

    public void ValidateAcquisition(SensingMode mode, Terminals terminals, bool requireSourceReadback)
    {
        if (!OutputEnabled || SensingMode != mode || Terminals != terminals || requireSourceReadback && !SourceReadback)
            throw new InvalidOperationException($"2450 buffer state differs from the acquisition: SOURSTAT={Source}, STAT={Measurement}.");
        if ((Issues & (ReadingQualityIssues.Protection | ReadingQualityIssues.OverTemperature)) != 0)
            throw new InvalidOperationException($"2450 acquisition reported {Issues}: SOURSTAT={Source}, STAT={Measurement}.");
    }
}

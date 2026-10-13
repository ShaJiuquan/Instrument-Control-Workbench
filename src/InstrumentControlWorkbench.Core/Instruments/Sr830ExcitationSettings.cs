using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

// SR830 SLVL is always nonzero. These settings never constitute an output-off proof.
public sealed record Sr830ExcitationSettings
{
    public Sr830ExcitationSettings(Quantity frequency, Quantity sineAmplitude, int harmonic = 1)
    {
        ExpectedReference = new(Sr830ReferenceSource.Internal, frequency, sineAmplitude, harmonic);
        Frequency = frequency; SineAmplitude = sineAmplitude; Harmonic = harmonic;
    }
    public Quantity Frequency { get; }
    public Quantity SineAmplitude { get; }
    public int Harmonic { get; }
    public Sr830ReferenceReadback ExpectedReference { get; }
}

using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Instruments;

public enum Sr830ReferenceSource { External, Internal }

// Observations only: SLVL readback is not a sample-terminal amplitude or off proof.
public sealed record Sr830ReferenceReadback
{
    public Sr830ReferenceReadback(Sr830ReferenceSource source, Quantity frequency, Quantity sineAmplitude, int harmonic)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        Sr830ReferenceParameters.Frequency.Validate(frequency); Sr830ReferenceParameters.SineAmplitude.Validate(sineAmplitude);
        if (harmonic is < 1 or > 19999 || harmonic * frequency.Value > 102000) throw new ArgumentOutOfRangeException(nameof(harmonic));
        Source = source; Frequency = frequency; SineAmplitude = sineAmplitude; Harmonic = harmonic;
    }
    public Sr830ReferenceSource Source { get; }
    public Quantity Frequency { get; }
    public Quantity SineAmplitude { get; }
    public int Harmonic { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureParameters() => new Dictionary<ParameterId, Quantity>
    {
        [Sr830ReferenceParameters.Source.Id] = new((int)Source, PhysicalUnit.Dimensionless),
        [Sr830ReferenceParameters.Frequency.Id] = Frequency,
        [Sr830ReferenceParameters.SineAmplitude.Id] = SineAmplitude,
        [Sr830ReferenceParameters.Harmonic.Id] = new(Harmonic, PhysicalUnit.Dimensionless),
    };
}

using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Devices;

// Registered wiring and relay qualification, never a recipe's permission flag.
// The complete disconnect set must remain safe to open even after unknown output drift.
public sealed record MatrixAcIsolationCapability
{
    public MatrixAcIsolationCapability(InstrumentId generator, IEnumerable<MatrixCrossPoint> connections, Quantity maxAmplitude, Quantity maxFrequency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generator.Value); ArgumentNullException.ThrowIfNull(connections);
        var points = connections.ToArray();
        if (points.Length is < 1 or > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length)
            throw new ArgumentException("AC isolation requires one to 4096 distinct disconnect crosspoints.", nameof(connections));
        if (maxAmplitude.Unit != PhysicalUnit.Volt || maxAmplitude.Value < 5)
            throw new ArgumentOutOfRangeException(nameof(maxAmplitude), "AC isolation must cover the SR830's full 5 V sine amplitude range.");
        if (maxFrequency.Unit != PhysicalUnit.Hertz || maxFrequency.Value < 102000)
            throw new ArgumentOutOfRangeException(nameof(maxFrequency), "AC isolation must cover the SR830's full 102000 Hz frequency range.");
        Generator = generator; Connections = Array.AsReadOnly(points); MaximumAmplitude = maxAmplitude; MaximumFrequency = maxFrequency;
    }
    public InstrumentId Generator { get; }
    public IReadOnlyList<MatrixCrossPoint> Connections { get; }
    public Quantity MaximumAmplitude { get; }
    public Quantity MaximumFrequency { get; }
}

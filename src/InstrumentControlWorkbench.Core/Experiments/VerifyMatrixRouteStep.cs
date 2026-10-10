using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Experiments;

// Verify the accepted route without switching relays or changing source output.
public sealed record VerifyMatrixRouteStep : ExperimentStep
{
    public VerifyMatrixRouteStep(StepId id, InstrumentId device, IEnumerable<MatrixCrossPoint> closed) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(closed);
        var points = closed.ToArray();
        if (points.Length > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length)
            throw new ArgumentException("Expected crosspoints must be unique and within the 4096-point budget.", nameof(closed));
        Device = device; Closed = Array.AsReadOnly(points);
    }
    public InstrumentId Device { get; }
    public IReadOnlyList<MatrixCrossPoint> Closed { get; }
}

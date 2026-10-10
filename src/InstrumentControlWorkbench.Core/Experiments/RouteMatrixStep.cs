using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record RouteMatrixStep : ExperimentStep
{
    public RouteMatrixStep(StepId id, InstrumentId device, IEnumerable<MatrixCrossPoint> closed, TimeSpan settle) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(closed);
        var points = closed.ToArray();
        if (points.Length > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length)
            throw new ArgumentException("Desired crosspoints must be unique and within the 4096-point software budget.", nameof(closed));
        if (settle < TimeSpan.Zero || settle > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(settle));
        Device = device; Closed = Array.AsReadOnly(points); Settle = settle;
    }
    public InstrumentId Device { get; }
    public IReadOnlyList<MatrixCrossPoint> Closed { get; }
    public TimeSpan Settle { get; }
}

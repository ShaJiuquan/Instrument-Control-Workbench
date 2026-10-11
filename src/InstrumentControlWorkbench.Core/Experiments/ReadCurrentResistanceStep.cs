using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum CurrentReference { ProgrammedSetpoint, MeasuredSourceReadback }

public sealed class MatrixRouteExpectation : IEquatable<MatrixRouteExpectation>
{
    public MatrixRouteExpectation(InstrumentId device, IEnumerable<MatrixCrossPoint> closed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(closed);
        var points = closed.ToArray();
        if (points.Length is < 1 or > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length)
            throw new ArgumentException("A measurement route requires bounded, nonempty, distinct crosspoints.");
        Device = device; Closed = Array.AsReadOnly(points.OrderBy(p => p.Slot).ThenBy(p => p.Row).ThenBy(p => p.Column).ToArray());
    }
    public InstrumentId Device { get; }
    public IReadOnlyList<MatrixCrossPoint> Closed { get; }
    public bool Equals(MatrixRouteExpectation? other) => other is not null && Device == other.Device && Closed.SequenceEqual(other.Closed);
    public override bool Equals(object? obj) => obj is MatrixRouteExpectation other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); hash.Add(Device); foreach (var point in Closed) hash.Add(point); return hash.ToHashCode(); }
}

// Acquisition only: setup, settling and owned/managed output stay ordinary steps.
public sealed record ReadCurrentResistanceStep : ExperimentStep
{
    public ReadCurrentResistanceStep(StepId id, InstrumentId source, InstrumentId meter, CurrentReference currentReference,
        ParameterId voltageColumn, ParameterId currentColumn, ParameterId resistanceColumn, Quantity minimumCurrent,
        PlanValue? expectedProgrammedCurrent = null, MatrixRouteExpectation? requiredRoute = null, IEnumerable<EnvironmentRead>? environmentReads = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Value); ArgumentException.ThrowIfNullOrWhiteSpace(meter.Value);
        ArgumentNullException.ThrowIfNull(voltageColumn); ArgumentNullException.ThrowIfNull(currentColumn); ArgumentNullException.ThrowIfNull(resistanceColumn);
        if (source == meter || !Enum.IsDefined(currentReference) || new[] { voltageColumn, currentColumn, resistanceColumn }.Distinct().Count() != 3 || minimumCurrent.Unit != PhysicalUnit.Ampere || minimumCurrent.Value < 0)
            throw new ArgumentException("Independent resistance requires distinct source/meter/columns and a nonnegative Ampere threshold.");
        if (expectedProgrammedCurrent is ConstantValue constant && (constant.Value.Unit != PhysicalUnit.Ampere || Math.Abs(constant.Value.Value) <= minimumCurrent.Value))
            throw new ArgumentException("Expected programmed current must be nonzero Ampere above the threshold.");
        if (requiredRoute is { } route && (route.Device == source || route.Device == meter)) throw new ArgumentException("Routing requires a separate matrix device.");
        Source = source; Meter = meter; CurrentReference = currentReference; VoltageColumn = voltageColumn; CurrentColumn = currentColumn;
        ResistanceColumn = resistanceColumn; MinimumCurrent = minimumCurrent; ExpectedProgrammedCurrent = expectedProgrammedCurrent; RequiredRoute = requiredRoute;
        EnvironmentReads = EnvironmentRead.Freeze(environmentReads, [voltageColumn, currentColumn, resistanceColumn]);
    }
    public InstrumentId Source { get; }
    public InstrumentId Meter { get; }
    public CurrentReference CurrentReference { get; }
    public ParameterId VoltageColumn { get; }
    public ParameterId CurrentColumn { get; }
    public ParameterId ResistanceColumn { get; }
    public Quantity MinimumCurrent { get; }
    public PlanValue? ExpectedProgrammedCurrent { get; }
    public MatrixRouteExpectation? RequiredRoute { get; }
    public IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
}

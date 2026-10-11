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

// Ordinary steps own initial setup/output. Explicit retry/recovery shares the
// same lifecycle and may park owned excitation after a terminal acquisition error.
public sealed record ReadCurrentResistanceStep : ResistanceAcquisitionStep
{
    public ReadCurrentResistanceStep(StepId id, InstrumentId source, InstrumentId meter, CurrentReference currentReference,
        ParameterId voltageColumn, ParameterId currentColumn, ParameterId resistanceColumn, Quantity minimumCurrent,
        PlanValue? expectedProgrammedCurrent = null, MatrixRouteExpectation? requiredRoute = null, IEnumerable<EnvironmentRead>? environmentReads = null, CurrentResistanceRetry? retry = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Value); ArgumentException.ThrowIfNullOrWhiteSpace(meter.Value);
        ArgumentNullException.ThrowIfNull(voltageColumn); ArgumentNullException.ThrowIfNull(currentColumn); ArgumentNullException.ThrowIfNull(resistanceColumn);
        if (source == meter || !Enum.IsDefined(currentReference) || new[] { voltageColumn, currentColumn, resistanceColumn }.Distinct().Count() != 3 || minimumCurrent.Unit != PhysicalUnit.Ampere || minimumCurrent.Value < 0)
            throw new ArgumentException("Independent resistance requires distinct source/meter/columns and a nonnegative Ampere threshold.");
        if (expectedProgrammedCurrent is ConstantValue constant && (constant.Value.Unit != PhysicalUnit.Ampere || Math.Abs(constant.Value.Value) <= minimumCurrent.Value))
            throw new ArgumentException("Expected programmed current must be nonzero Ampere above the threshold.");
        if (requiredRoute is { } route && (route.Device == source || route.Device == meter)) throw new ArgumentException("Routing requires a separate matrix device.");
        Retry = retry; Source = source; Meter = meter; CurrentReference = currentReference; VoltageColumn = voltageColumn; CurrentColumn = currentColumn;
        ResistanceColumn = resistanceColumn; MinimumCurrent = minimumCurrent; ExpectedProgrammedCurrent = expectedProgrammedCurrent; RequiredRoute = requiredRoute;
        EnvironmentReads = EnvironmentRead.Freeze(environmentReads, [voltageColumn, currentColumn, resistanceColumn]);
    }
    public CurrentResistanceRetry? Retry { get; }
    public InstrumentId Source { get; }
    public override InstrumentId Device => Source;
    public override SourceFunction Function => SourceFunction.Current;
    public InstrumentId Meter { get; }
    public CurrentReference CurrentReference { get; }
    public override ParameterId VoltageColumn { get; }
    public override ParameterId CurrentColumn { get; }
    public override ParameterId ResistanceColumn { get; }
    public override Quantity MinimumCurrent { get; }
    public PlanValue? ExpectedProgrammedCurrent { get; }
    public MatrixRouteExpectation? RequiredRoute { get; }
    public override IReadOnlyList<EnvironmentRead> EnvironmentReads { get; }
}

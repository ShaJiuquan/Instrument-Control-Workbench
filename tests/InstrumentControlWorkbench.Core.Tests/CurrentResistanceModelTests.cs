using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class CurrentResistanceModelTests
{
    private static readonly InstrumentId Source = new("source"), Meter = new("meter");
    private static ReadCurrentResistanceStep Step(CurrentReference reference = CurrentReference.ProgrammedSetpoint)
        => new(new("read"), Source, Meter, reference, new("V"), new("I"), new("R"), new(2.220446049250313e-16, PhysicalUnit.Ampere), new ConstantValue(new(1e-5, PhysicalUnit.Ampere)));
    [Theory]
    [InlineData(CurrentReference.ProgrammedSetpoint, 1e-5, 100)]
    [InlineData(CurrentReference.MeasuredSourceReadback, 8e-6, 125)]
    [InlineData(CurrentReference.MeasuredSourceReadback, -8e-6, -125)]
    public void ResistancePreservesReferenceAndSignedCurrentWithoutInventingSimultaneity(CurrentReference reference, double current, double expected)
    {
        var time = DateTimeOffset.UtcNow; var e = new IndependentResistanceEvidence(Step(reference), new(SourceFunction.Current, 1e-5, 10), new(current, PhysicalUnit.Ampere), new(0.001, PhysicalUnit.Volt), time, time.AddMilliseconds(1), time, time.AddMilliseconds(2));
        Assert.Equal(expected, e.Resistance.Value, 10); Assert.Equal(reference, e.CurrentReference); Assert.NotEqual(e.SourceCapturedAt, e.VoltageCapturedAt);
    }
    [Theory]
    [InlineData("sameDevice")]
    [InlineData("sameColumn")]
    [InlineData("thresholdUnit")]
    [InlineData("reference")]
    public void InvalidIdentityAndNumericPolicyCannotBecomeAReadStep(string failure)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ReadCurrentResistanceStep(new("read"), Source, failure == "sameDevice" ? Source : Meter,
            failure == "reference" ? (CurrentReference)3 : CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new(failure == "sameColumn" ? "I" : "R"), new(0, failure == "thresholdUnit" ? PhysicalUnit.Volt : PhysicalUnit.Ampere)));
    }
    [Theory]
    [InlineData("programmedMismatch")]
    [InlineData("zero")]
    [InlineData("unit")]
    [InlineData("function")]
    [InlineData("before")]
    [InlineData("order")]
    [InlineData("after")]
    public void InvalidExcitationAndCaptureIntervalCannotBecomeEvidence(string failure)
    {
        var time = DateTimeOffset.UtcNow;
        Assert.ThrowsAny<ArgumentException>(() => new IndependentResistanceEvidence(Step(), new(failure == "function" ? SourceFunction.Voltage : SourceFunction.Current, 1e-5, failure == "function" ? 1e-4 : 10),
            new(failure == "zero" ? 0 : failure == "programmedMismatch" ? 8e-6 : 1e-5, failure == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Ampere), new(0.001, PhysicalUnit.Volt),
            failure == "before" ? time.AddMilliseconds(-1) : time, failure == "order" ? time.AddMilliseconds(-1) : time.AddMilliseconds(1), time, failure == "after" ? time : time.AddMilliseconds(2)));
    }
    [Fact]
    public void RouteIsCanonicalImmutableAndRequiresDistinctNonemptyPoints()
    {
        MatrixCrossPoint[] points = [new(1, 2, 2), new(1, 1, 1)]; var route = new MatrixRouteExpectation(new("matrix"), points); points[0] = new(1, 3, 3);
        var same = new MatrixRouteExpectation(new("matrix"), [new(1, 2, 2), new(1, 1, 1)]); Assert.Equal(route, same); Assert.Equal(route.GetHashCode(), same.GetHashCode());
        Assert.Equal(1, route.Closed[0].Row); Assert.Throws<ArgumentException>(() => new MatrixRouteExpectation(new("matrix"), []));
        Assert.Throws<ArgumentException>(() => new MatrixRouteExpectation(new("matrix"), [new(1, 1, 1), new(1, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new ReadCurrentResistanceStep(new("read"), Source, Meter, CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere), requiredRoute: new(Source, [new(1, 1, 1)])));
    }
    [Theory]
    [InlineData("resistance")]
    [InlineData("current")]
    [InlineData("missingEvidence")]
    [InlineData("qualityTime")]
    [InlineData("fakeStatus")]
    public void SharedLiveArchiveValidationRejectsTamperedIndependentRows(string failure)
    {
        var step = Step(); var time = DateTimeOffset.UtcNow;
        var e = new IndependentResistanceEvidence(step, new(SourceFunction.Current, 1e-5, 10), new(1e-5, PhysicalUnit.Ampere), new(0.001, PhysicalUnit.Volt), time, time.AddMilliseconds(1), time, time.AddMilliseconds(2));
        var values = new Dictionary<ParameterId, Quantity> { [step.CurrentColumn] = new(failure == "current" ? 8e-6 : 1e-5, PhysicalUnit.Ampere), [step.VoltageColumn] = e.Voltage, [step.ResistanceColumn] = new(failure == "resistance" ? 99 : 100, PhysicalUnit.Ohm) };
        var schema = new DataSetSchema([new(new(step.VoltageColumn, "V", PhysicalUnit.Volt, ParameterAccess.Read)), new(new(step.CurrentColumn, "I", PhysicalUnit.Ampere, ParameterAccess.Read)), new(new(step.ResistanceColumn, "R", PhysicalUnit.Ohm, ParameterAccess.Read), [step.VoltageColumn, step.CurrentColumn])]);
        var row = new MeasurementDataRow(time.AddMilliseconds(2), values,
            quality: [new(step.Id, Source, step.CurrentColumn, time, failure == "fakeStatus" ? new(136, 0) : null), new(step.Id, Meter, step.VoltageColumn, failure == "qualityTime" ? time : time.AddMilliseconds(1), null, meterConfiguration: new(1, null, 1, false, 10, AveragingMode.Repeat, 0.1))],
            independentResistance: failure == "missingEvidence" ? [] : [e]);
        Assert.ThrowsAny<ArgumentException>(() => new RunDataRowValidator(new("Independent", step), schema).Validate(row));
    }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Sr830AcEvidenceTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly MatrixCrossPoint P = new(1, 1, 1);
    private static ReadSr830AcFrameStep Step(string id = "ac", string prefix = "raw") => new(new(id), new("generator"), new("isolator"), [P], new(new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt)), Sr830InputMode.VoltageAB, new(prefix), TimeSpan.Zero);
    private static Sr830AcEvidence Evidence(ReadSr830AcFrameStep step, string? device = null) => new(step.Id, new(device ?? step.Device.Value), step.Isolator, step.Connections, step.Excitation.ExpectedReference, T.AddSeconds(2),
        new(step.Isolator, T, []), new(step.Isolator, T.AddSeconds(1), [P]), new(step.Isolator, T.AddSeconds(3), [P]), new(step.Isolator, T.AddSeconds(4), []));
    private static MeasurementDataRow Row(ReadSr830AcFrameStep step, IEnumerable<Sr830AcEvidence>? evidence = null, bool quality = true)
    {
        var observation = new Sr830FrameObservation(new(new(.001, PhysicalUnit.Volt), new(-.002, PhysicalUnit.Volt), new(.0023, PhysicalUnit.Volt), new(-63, PhysicalUnit.Degree), T.AddSeconds(2)), new(Sr830InputMode.VoltageAB, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12), step.Excitation.ExpectedReference);
        return new(T.AddSeconds(4), step.Frame.CaptureValues(observation), quality: quality ? step.Frame.Channels.Take(4).Select(c => new AcquisitionQuality(step.Id, step.Device, c.Column, T.AddSeconds(2), null)) : [], sr830Ac: evidence);
    }
    private static DataSetSchema Schema(ReadSr830AcFrameStep step) => new(step.Frame.Channels.Select(c => new DataColumn(new(c.Column, c.Column.Value, Sr830Parameters.WithReference.Single(p => p.Id == c.Address.Parameter).Unit, ParameterAccess.Read))));

    [Fact]
    public void CompleteIsolatedFrameValidatesAndStructuralCopiesMatch()
    {
        var step = Step(); var evidence = Evidence(step); var copy = Evidence(step);
        Assert.True(evidence.ContentEquals(copy));
        new RunDataRowValidator(new("AC", step), Schema(step)).Validate(Row(step, [copy]));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-device")]
    [InlineData("extra")]
    [InlineData("missing-quality")]
    public void ActiveFrameRejectsMissingOrUnmatchedProvenance(string damage)
    {
        var step = Step(); Sr830AcEvidence[] evidence = damage switch { "missing" => Array.Empty<Sr830AcEvidence>(), "wrong-device" => [Evidence(step, "other-generator")], "extra" => [Evidence(step), Evidence(Step("extra", "extra"))], _ => [Evidence(step)] };
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(new("AC", step), Schema(step)).Validate(Row(step, evidence, damage != "missing-quality")));
    }
    [Theory]
    [InlineData("step")]
    [InlineData("device")]
    [InlineData("isolator")]
    [InlineData("timestamp")]
    public void DefaultIdentitiesAndCaptureTimesReject(string damage)
    {
        var step = Step(); var e = Evidence(step);
        Assert.ThrowsAny<ArgumentException>(() => new Sr830AcEvidence(damage == "step" ? null! : step.Id, damage == "device" ? default : step.Device, damage == "isolator" ? default : step.Isolator, step.Connections, e.Reference, damage == "timestamp" ? default : e.FrameCapturedAt, e.BeforeConfigurationOpen, e.BeforeFrameClosed, e.AfterFrameClosed, e.AfterAcquisitionOpen));
    }
    [Fact]
    public void UnrelatedRouteDriftCannotBecomeAnIsolationProof()
    {
        var step = Step(); var e = Evidence(step);
        Assert.Throws<ArgumentException>(() => new Sr830AcEvidence(step.Id, step.Device, step.Isolator, step.Connections, e.Reference, e.FrameCapturedAt, e.BeforeConfigurationOpen, e.BeforeFrameClosed, e.AfterFrameClosed, new(step.Isolator, T.AddSeconds(4), [new(1, 2, 2)])));
        Assert.Throws<ArgumentException>(() => new Sr830AcRouteObservation(step.Isolator, default, []));
    }
    [Fact]
    public void WidePointPreservesEachMembersOriginalAcEvidence()
    {
        var first = Step(); var second = Step("second", "second");
        var e1 = Evidence(first); var e2 = Evidence(second);
        MeasurementDataRow WithElapsed(ReadSr830AcFrameStep step, Sr830AcEvidence evidence, double elapsed)
        {
            var row = Row(step, [evidence]); var values = new Dictionary<ParameterId, Quantity>(row.Values) { [new("elapsed")] = new(elapsed, PhysicalUnit.Second) };
            return new(row.Timestamp, values, quality: row.Quality, sr830Ac: row.Sr830Ac);
        }
        var group = new MeasurementGroupStep(new("point"), [new("first", first), new("second", second)]);
        var wide = MeasurementGroupSummary.BuildRow(group, 1, [WithElapsed(first, e1, .1), WithElapsed(second, e2, .2)]);
        Assert.Equal(2, wide.Sr830Ac.Count); Assert.Same(e1, wide.Sr830Ac[0]); Assert.Same(e2, wide.Sr830Ac[1]); Assert.Equal(8, wide.Quality.Count);
    }
    [Fact]
    public void CalculationRetainsTheOriginalAcquisitionEvidence()
    {
        var step = Step(); var evidence = Evidence(step); var input = Row(step, [evidence]);
        var calculation = new CalculatedMeasurementStep(new("calculation"), step, [new(new("result"), new VariableValue(step.Frame.Channels[0].Column))]);
        var result = CalculationSummary.BuildRow(calculation, 1, input);
        Assert.Same(evidence, Assert.Single(result.Sr830Ac)); Assert.Equal(input.Quality, result.Quality);
    }
}

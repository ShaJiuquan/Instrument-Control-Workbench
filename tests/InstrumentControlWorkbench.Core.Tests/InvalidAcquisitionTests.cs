using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class InvalidAcquisitionTests
{
    private static readonly Keithley2182Settings Meter = new(1, null, 1, false, 10, AveragingMode.Repeat, 1);
    private static readonly SampleGeometry Geometry = new(new(2, PhysicalUnit.Meter), new(4, PhysicalUnit.Meter), new(0.1, PhysicalUnit.Meter));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialHallUsesOnlyActuallyAvailableLongitudinalAndTransverseInputs(int missing)
    {
        Quantity?[] input = [new(100, PhysicalUnit.Ohm), new(-10, PhysicalUnit.Ohm)];
        if (missing < 2) input[missing] = null; else { input[0] = null; input[1] = null; }
        var result = SampleResistanceAnalysis.CalculateAvailable(SampleTopology.HallBar, Geometry, input);
        Assert.Equal(SampleAnalysisIssues.FailedAcquisition, result.Issues);
        Assert.Equal(missing == 1, result.Values.ContainsKey(SampleDerivedQuantity.LongitudinalResistance));
        Assert.Equal(missing == 0, result.Values.ContainsKey(SampleDerivedQuantity.TransverseResistance));
        Assert.Equal(missing == 1, result.Values.ContainsKey(SampleDerivedQuantity.SheetResistance));
        if (missing == 1) { Assert.Equal(50, result.Values[SampleDerivedQuantity.SheetResistance].Value, 10); Assert.Equal(5, result.Values[SampleDerivedQuantity.Resistivity].Value, 10); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void MissingVdpInputSuppressesItsEntireFourMemberMeanAndSheet(int missing)
    {
        var input = Enumerable.Range(0, 8).Select(i => (Quantity?)new Quantity(i < 4 ? (i % 2 == 0 ? 100 : -100) : 200, PhysicalUnit.Ohm)).ToArray(); input[missing] = null;
        var result = SampleResistanceAnalysis.CalculateAvailable(SampleTopology.VanDerPauw, Geometry, input);
        Assert.Equal(SampleAnalysisIssues.FailedAcquisition, result.Issues); Assert.Single(result.Values);
        var available = missing < 4 ? SampleDerivedQuantity.VerticalMeanResistance : SampleDerivedQuantity.HorizontalMeanResistance;
        Assert.Equal(missing < 4 ? 200 : 100, result.Values[available].Value);
    }

    [Fact]
    public void MissingAndActuallyMeasuredZeroRemainDistinct()
    {
        Quantity?[] input = [null, new(1, PhysicalUnit.Ohm), new(1, PhysicalUnit.Ohm), new(1, PhysicalUnit.Ohm), new(0, PhysicalUnit.Ohm), new(0, PhysicalUnit.Ohm), new(0, PhysicalUnit.Ohm), new(0, PhysicalUnit.Ohm)];
        var result = SampleResistanceAnalysis.CalculateAvailable(SampleTopology.VanDerPauw, Geometry, input);
        Assert.Equal(SampleAnalysisIssues.FailedAcquisition | SampleAnalysisIssues.NonPositiveVanDerPauwMean, result.Issues);
        Assert.Equal(0, result.Values[SampleDerivedQuantity.VerticalMeanResistance].Value); Assert.DoesNotContain(SampleDerivedQuantity.HorizontalMeanResistance, result.Values.Keys);
        Assert.Throws<ArgumentException>(() => SampleResistanceAnalysis.CalculateAvailable(SampleTopology.HallBar, Geometry, [new(1, PhysicalUnit.Volt), null]));
    }

    [Theory]
    [InlineData("noPolicy")]
    [InlineData("code")]
    [InlineData("time")]
    [InlineData("off")]
    [InlineData("retrySettings")]
    public void InvalidPointCannotBeConstructedWithoutEligibleOrderedCleanupProof(string damage)
    {
        var t = DateTimeOffset.UtcNow; var read = Read(damage == "noPolicy" ? null : new(true, TimeSpan.Zero, true));
        var retry = damage == "retrySettings" ? new CurrentResistanceRetryEvidence(InstrumentReadFailureKind.ReadTimeout, null, "Prior failure", t.AddSeconds(-4), t.AddSeconds(-3), t.AddSeconds(-1), t.AddSeconds(-2), Meter.WithParameter(Keithley2182Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless))) : null;
        Assert.Throws<ArgumentException>(() => new IndependentResistanceFailure(read, new(CurrentSourceModel.Keithley6221, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)),
            damage == "code" ? InstrumentReadFailureKind.CommandRejected : InstrumentReadFailureKind.Overrange, damage == "code" ? 999 : null, "Read failed", Meter,
            t, t.AddSeconds(1), t.AddSeconds(2), t.AddSeconds(3), damage == "time" ? t.AddSeconds(1) : t.AddSeconds(4), damage == "off" ? null : t.AddSeconds(3), retry));
    }

    [Fact]
    public void FailureListIsFrozenAndCannotAlsoClaimSuccessfulEvidenceForTheSameInput()
    {
        var t = DateTimeOffset.UtcNow; var read = Read(new(true, TimeSpan.Zero, true));
        var config = new CurrentExcitationConfiguration(CurrentSourceModel.Keithley6221, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt));
        var failure = new IndependentResistanceFailure(read, config, InstrumentReadFailureKind.Overrange, null, "Read failed", Meter, t, t.AddSeconds(1), t.AddSeconds(2), t.AddSeconds(3), t.AddSeconds(4), t.AddSeconds(3));
        var failures = new[] { failure }; var row = new MeasurementDataRow(t.AddSeconds(5), new Dictionary<ParameterId, Quantity>(), acquisitionFailures: failures); failures[0] = null!;
        Assert.Equal(failure, Assert.Single(row.AcquisitionFailures)); Assert.Equal(ReadingQualityState.ContainsFailedAcquisitions, row.QualityState);
        var evidence = new IndependentResistanceEvidence(read, config, new(1e-5, PhysicalUnit.Ampere), new(0.001, PhysicalUnit.Volt), t, t.AddSeconds(1), t, t.AddSeconds(2));
        Assert.Throws<ArgumentException>(() => new MeasurementDataRow(t.AddSeconds(5), new Dictionary<ParameterId, Quantity>(), independentResistance: [evidence], acquisitionFailures: [failure]));
        Assert.Throws<ArgumentException>(() => failure.Validate(read, new(t.AddSeconds(5), new Dictionary<ParameterId, Quantity> { [read.VoltageColumn] = new(0, PhysicalUnit.Volt) }), CurrentSourceModel.Keithley6221));
    }

    private static ReadCurrentResistanceStep Read(CurrentResistanceRetry? policy) => new(new("read"), new("source"), new("meter"), CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(1e-16, PhysicalUnit.Ampere), new ConstantValue(new(1e-5, PhysicalUnit.Ampere)), retry: policy);
}

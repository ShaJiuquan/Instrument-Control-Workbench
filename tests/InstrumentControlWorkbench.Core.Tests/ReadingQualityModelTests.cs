using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ReadingQualityModelTests
{
    [Theory]
    [InlineData(200, 0, ReadingQualityIssues.None)]
    [InlineData(200, 256, ReadingQualityIssues.None)]
    [InlineData(232, 0, ReadingQualityIssues.SourceLimited)]
    [InlineData(200, 1, ReadingQualityIssues.Questionable)]
    [InlineData(204, 0, ReadingQualityIssues.Protection)]
    [InlineData(216, 0, ReadingQualityIssues.OverTemperature)]
    [InlineData(200, 16, ReadingQualityIssues.MeasurementLimit)]
    [InlineData(200, 32, ReadingQualityIssues.MeasurementLimit)]
    [InlineData(200, 64, ReadingQualityIssues.MeasurementLimit)]
    [InlineData(200, 128, ReadingQualityIssues.MeasurementLimit)]
    [InlineData(200, 2, ReadingQualityIssues.UnexpectedOrigin)]
    [InlineData(200, 4, ReadingQualityIssues.UnexpectedOrigin)]
    [InlineData(201, 0, ReadingQualityIssues.UnknownStatusBits)]
    [InlineData(456, 0, ReadingQualityIssues.UnknownStatusBits)]
    [InlineData(200, 512, ReadingQualityIssues.UnknownStatusBits)]
    public void RawWordsHaveDocumentedIndependentMeaning(ushort source, ushort measurement, ReadingQualityIssues issues)
    {
        var status = new Keithley2450ReadingStatus(source, measurement);
        Assert.Equal(source, status.Source); Assert.Equal(measurement, status.Measurement); Assert.Equal(issues, status.Issues);
    }
    [Fact]
    public void InformationalAndCaptureBitsAreSeparateFromQualityIssues()
    {
        var status = new Keithley2450ReadingStatus(200, 264);
        Assert.True(status.SourceReadback); Assert.True(status.OutputEnabled); Assert.Equal(SensingMode.FourWire, status.SensingMode); Assert.Equal(Terminals.Front, status.Terminals);
        Assert.Equal(ReadingQualityIssues.None, status.Issues); status.ValidateAcquisition(SensingMode.FourWire, Terminals.Front, true);
        var noReadback = new Keithley2450ReadingStatus(128, 0); noReadback.ValidateAcquisition(SensingMode.TwoWire, Terminals.Rear, false);
        Assert.Throws<InvalidOperationException>(() => noReadback.ValidateAcquisition(SensingMode.TwoWire, Terminals.Rear, true));
    }
    [Fact]
    public void RowFreezesEvidenceAndNeverTreatsUnobservedColumnsAsObserved()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new List<AcquisitionQuality> { new(new("read"), new("smu"), new("current"), now, null) };
        var row = new MeasurementDataRow(now, new Dictionary<ParameterId, Quantity>(), quality: input); input.Clear();
        Assert.Single(row.Quality); Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); Assert.Equal(0, row.ObservedQualityColumns); Assert.Equal(ReadingQualityIssues.None, row.QualityIssues);
        Assert.Empty(new MeasurementDataRow(now, new Dictionary<ParameterId, Quantity>()).Quality);
        Assert.Throws<ArgumentException>(() => new MeasurementDataRow(now, new Dictionary<ParameterId, Quantity>(), quality: Enumerable.Repeat(row.Quality[0], 4097)));
        Assert.Throws<ArgumentException>(() => new MeasurementDataRow(now, new Dictionary<ParameterId, Quantity>(), quality: new AcquisitionQuality[] { null! }));
    }
    [Fact]
    public void MixedObservationStateKeepsKnownWarningsAndUnobservedEvidenceSeparate()
    {
        var now = DateTimeOffset.UtcNow;
        var quality = new AcquisitionQuality[] { new(new("read"), new("one"), new("a"), now, new(200, 1)), new(new("read"), new("two"), new("b"), now, null) };
        var row = new MeasurementDataRow(now, new Dictionary<ParameterId, Quantity>(), quality: quality);
        Assert.Equal(ReadingQualityState.PartiallyObserved, row.QualityState); Assert.Equal(1, row.ObservedQualityColumns); Assert.Equal(ReadingQualityIssues.Questionable, row.QualityIssues);
    }

}

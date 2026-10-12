using InstrumentControlWorkbench.App.Controls;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class HistoryPlotGeometryTests
{
    [Fact]
    public void RecordOrderZeroCoordinatesAndMissingDataArePreservedWithoutBridgingGaps()
    {
        var samples = new[] { Sample(1, 3, 0), Sample(2, 1, 1), Sample(3, 4, null), Sample(4, 2, 2) };
        var g = HistoryPlotGeometry.Create(Page(samples))!;
        Assert.Equal(new long[] { 1, 2, 4 }, g.Vertices.Select(v => v.Sample.RowIndex));
        Assert.Same(samples[0], g.Vertices[0].Sample); Assert.Equal(0, g.Vertices[0].Y);
        Assert.Equal(new[] { false, true, false }, g.Vertices.Select(v => v.ConnectPrevious));
        Assert.True(g.Vertices[0].X > g.Vertices[1].X); Assert.Equal(3, g.Vertices.Count);
    }
    [Fact]
    public void FailedCoordinatesProvenanceChangesAndDiscontinuousSourceRecordsBreakConnections()
    {
        var samples = new[] { Sample(1, 0, 0), Sample(2, 1, 1, quality: ReadingQualityState.ContainsFailedAcquisitions),
            Sample(3, 2, 2), Sample(4, 3, 3, HistoryPlotRowKind.SampleAnalysis), Sample(5, 4, 4, HistoryPlotRowKind.MeasurementGroup), Sample(9, 5, 5) };
        var vertices = HistoryPlotGeometry.Create(Page(samples))!.Vertices;
        Assert.Equal(samples.Length, vertices.Count); Assert.All(vertices, v => Assert.False(v.ConnectPrevious));
        Assert.Equal(ReadingQualityState.ContainsFailedAcquisitions, vertices[1].Sample.QualityState);
        Assert.Equal(HistoryPlotRowKind.SampleAnalysis, vertices[3].Sample.Kind); Assert.Equal(HistoryPlotRowKind.MeasurementGroup, vertices[4].Sample.Kind);
    }
    [Fact]
    public void WarningAndUnobservedQualityRemainAttachedToTheirActualSamples()
    {
        var warning = Sample(2, 1, 1, quality: ReadingQualityState.ObservedWithIssues) with { QualityIssues = ReadingQualityIssues.SourceLimited };
        var samples = new[] { Sample(1, 0, 0), warning, Sample(3, 2, 2, quality: ReadingQualityState.PartiallyObserved) };
        var g = HistoryPlotGeometry.Create(Page(samples))!;
        Assert.Same(warning, g.Vertices[1].Sample); Assert.Equal(ReadingQualityIssues.SourceLimited, g.Vertices[1].Sample.QualityIssues);
        Assert.Equal(new[] { false, true, true }, g.Vertices.Select(v => v.ConnectPrevious));
    }
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1e-320, 2e-320)]
    [InlineData(-1.7976931348623157e308, 1.7976931348623157e308)]
    [InlineData(8e307, 1.7976931348623157e308)]
    public void FiniteScientificExtremesNormalizeAndProduceFiniteAxisTicks(double minimum, double maximum)
    {
        var range = new HistoryAxisRange(minimum, maximum);
        Assert.Equal(minimum == maximum ? .5 : 0, range.Normalize(minimum));
        Assert.Equal(minimum == maximum ? .5 : 1, range.Normalize(maximum));
        foreach (var fraction in new[] { 0d, .25, .5, .75, 1 })
        {
            var tick = range.At(fraction); Assert.True(double.IsFinite(tick)); Assert.InRange(tick, minimum, maximum);
        }
    }
    [Fact]
    public void AConstantZeroPageKeepsItsPointAtCenterAndEmptyPagesInventNoCoordinates()
    {
        var g = HistoryPlotGeometry.Create(Page([Sample(1, 0, 0)]))!; var point = Assert.Single(g.Vertices);
        Assert.Equal(.5, point.X); Assert.Equal(.5, point.Y); Assert.Equal(0, g.XRange.At(.25));
        Assert.Null(HistoryPlotGeometry.Create(null)); Assert.Null(HistoryPlotGeometry.Create(Page([])));
        Assert.Null(HistoryPlotGeometry.Create(Page([Sample(1, 1, null)])));
    }
    private static HistoryPlotSample Sample(long index, double x, double? y, HistoryPlotRowKind kind = HistoryPlotRowKind.Reading,
        ReadingQualityState quality = ReadingQualityState.Unobserved) => new(index, DateTimeOffset.UnixEpoch.AddSeconds(index),
            new(x, PhysicalUnit.Dimensionless), y is { } value ? new(value, PhysicalUnit.Dimensionless) : null, kind, quality, ReadingQualityIssues.None, 0);
    private static HistoryPlotPage Page(HistoryPlotSample[] samples)
    {
        var x = new ParameterDescriptor(new("x"), "X", PhysicalUnit.Dimensionless, ParameterAccess.Read); var y = new ParameterDescriptor(new("y"), "Y", PhysicalUnit.Dimensionless, ParameterAccess.Read);
        var data = new RunDataSet(RunId.New(), "geometry", DateTimeOffset.UnixEpoch, new([new(x), new(y, [x.Id], optional: true)]), new Dictionary<string, Quantity>(), []);
        return new(new(data.Capture(), samples.Length, null), x, y, samples, 0, samples.Length, null, false, new(data.Id, new('A', 64), 0, new('B', 64), null), 0);
    }
}

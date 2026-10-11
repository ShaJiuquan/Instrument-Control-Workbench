using System.Text;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class RunArchiveTests
{
    [Fact]
    public async Task PlotPagesPreserveUnitsSourceOrderGapsAndOriginalRowIndicesAfterRestart()
    {
        using var folder = new TemporaryFolder(); RunId id;
        await using (var archive = new FileRunArchive(folder.Path)) id = await SaveSparsePlotAsync(archive);
        await using var fresh = new FileRunArchive(folder.Path); var history = new RunHistoryService(fresh);
        var first = await history.ReadPlotPageAsync(id, new(pageSize: 3), new("x"), new("y"), CancellationToken.None);
        Assert.Equal("Voltage", first.XAxis.Label); Assert.Equal(PhysicalUnit.Volt, first.XAxis.Unit);
        Assert.Equal("Current", first.YAxis.Label); Assert.Equal(PhysicalUnit.Ampere, first.YAxis.Unit);
        Assert.Equal(new long[] { 1, 2, 3 }, first.Samples.Select(s => s.RowIndex));
        Assert.Equal(new double?[] { 2, -1, null }, first.Samples.Select(s => s.X?.Value));
        Assert.Equal(new double?[] { 4, null, 6 }, first.Samples.Select(s => s.Y?.Value));
        Assert.True(first.Samples[0].HasCoordinates); Assert.False(first.Samples[1].HasCoordinates);
        Assert.False(first.Samples[2].HasCoordinates); Assert.Empty(first.Metadata.Header.Rows);
        Assert.All(first.Samples, s => { Assert.Equal(ReadingQualityState.Unobserved, s.QualityState); Assert.Equal(ReadingQualityIssues.None, s.QualityIssues); Assert.Equal(0, s.ObservedQualityColumns); Assert.Equal(HistoryPlotRowKind.Reading, s.Kind); });
        Assert.Throws<NotSupportedException>(() => ((IList<HistoryPlotSample>)first.Samples).Clear());
        var second = await history.ReadPlotPageAsync(id, new(first.NextOffset!.Value, 3, first.Version), new("x"), new("y"), CancellationToken.None);
        Assert.Equal(new long[] { 4, 5 }, second.Samples.Select(s => s.RowIndex));
        Assert.Null(second.Samples[0].X); Assert.Null(second.Samples[0].Y); Assert.False(second.Samples[0].HasCoordinates);
        Assert.Equal(new Quantity(2, PhysicalUnit.Volt), second.Samples[1].X);
        Assert.Equal(first.Metadata.Header.StartedAt.AddSeconds(5), second.Samples[1].Timestamp);
        Assert.Equal(first.Version, second.Version); Assert.False(second.HasMore); Assert.Null(second.NextOffset); Assert.Equal(5, second.ObservedRowCount);
    }

    [Fact]
    public async Task PlotByteBudgetAndNextOffsetCountSourceRecordsIncludingGaps()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var id = await SaveSparsePlotAsync(archive);
        var records = await File.ReadAllLinesAsync(System.IO.Path.Combine(folder.Path, id.Value.ToString("N"), "rows.ndjson"));
        var budget = Encoding.UTF8.GetByteCount(records[0]) + Encoding.UTF8.GetByteCount(records[1]) + 2;
        var history = new RunHistoryService(archive);
        var page = await history.ReadPlotPageAsync(id, new(pageSize: 5, maximumDataBytes: budget), new("x"), new("y"), CancellationToken.None);
        Assert.Equal(2, page.Samples.Count); Assert.False(page.Samples[^1].HasCoordinates); Assert.Equal(2, page.NextOffset); Assert.Equal(budget, page.ReturnedDataBytes);
        var next = await history.ReadPlotPageAsync(id, new(page.NextOffset!.Value, 1, page.Version), new("x"), new("y"), CancellationToken.None);
        Assert.Equal(3, Assert.Single(next.Samples).RowIndex); Assert.Equal(3, next.NextOffset);
        await Assert.ThrowsAsync<IOException>(() => history.ReadPlotPageAsync(id, new(maximumDataBytes: 1), new("x"), new("y"), CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlotRejectsUnknownAxesInsteadOfTreatingThemAsMissingData(bool xUnknown)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var id = await SaveSparsePlotAsync(archive);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new RunHistoryService(archive).ReadPlotPageAsync(id,
            new(), new(xUnknown ? "unknown" : "x"), new(xUnknown ? "y" : "unknown"), CancellationToken.None));
        Assert.Equal(xUnknown ? "xColumn" : "yColumn", error.ParamName);
    }

    [Fact]
    public async Task PlotCannotMixVersionedPagesOrReturnBeforeWholeArchiveIntegrityCheck()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var id = await SaveSparsePlotAsync(archive);
        var history = new RunHistoryService(archive); var first = await history.ReadPlotPageAsync(id, new(pageSize: 1), new("x"), new("y"), CancellationToken.None);
        var run = System.IO.Path.Combine(folder.Path, id.Value.ToString("N")); var headerPath = System.IO.Path.Combine(run, "metadata.json");
        var header = JsonNode.Parse(await File.ReadAllTextAsync(headerPath))!; header["name"] = "replaced"; await File.WriteAllTextAsync(headerPath, header.ToJsonString());
        await Assert.ThrowsAsync<IOException>(() => history.ReadPlotPageAsync(id, new(1, 1, first.Version), new("x"), new("y"), CancellationToken.None));
        var current = await history.ReadPlotPageAsync(id, new(pageSize: 1), new("x"), new("y"), CancellationToken.None); Assert.NotEqual(first.Version, current.Version);
        var rowsPath = System.IO.Path.Combine(run, "rows.ndjson"); var rows = await File.ReadAllLinesAsync(rowsPath); rows[^1] = "{broken"; await File.WriteAllTextAsync(rowsPath, string.Join('\n', rows) + "\n");
        Assert.NotNull(await Record.ExceptionAsync(() => history.ReadPlotPageAsync(id, new(pageSize: 1), new("x"), new("y"), CancellationToken.None)));
        Assert.True(Assert.Single(first.Samples).HasCoordinates);
    }

    [Fact]
    public async Task EmptyUnfinalizedAndCanceledPlotReadsDoNotInventCoordinatesOrCreateStorage()
    {
        using var folder = new TemporaryFolder(); var archivePath = System.IO.Path.Combine(folder.Path, "archive");
        await using var archive = new FileRunArchive(archivePath); var history = new RunHistoryService(archive);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.ReadPlotPageAsync(RunId.New(), new(), new("x"), new("x"), canceled.Token)); Assert.False(Directory.Exists(archivePath));
        var data = DataSet(); await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        var empty = await history.ReadPlotPageAsync(data.Id, new(int.MaxValue), new("x"), new("x"), CancellationToken.None);
        Assert.Empty(empty.Samples); Assert.False(empty.HasMore); Assert.False(empty.Metadata.IsFinalized); Assert.Null(empty.Version.FinalSha256); Assert.Equal(0, empty.ObservedRowCount);
        await Assert.ThrowsAsync<ArgumentNullException>(() => history.ReadPlotPageAsync(data.Id, new(), null!, new("x"), CancellationToken.None));
    }

    private static async Task<RunId> SaveSparsePlotAsync(FileRunArchive archive)
    {
        var data = new RunDataSet(RunId.New(), "sparse curve", DateTimeOffset.UtcNow,
            new([new(new ParameterDescriptor(new("x"), "Voltage", PhysicalUnit.Volt, ParameterAccess.Read), optional: true),
                new(new ParameterDescriptor(new("y"), "Current", PhysicalUnit.Ampere, ParameterAccess.Read), optional: true)]), new Dictionary<string, Quantity>(), []);
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        double?[] xs = [2, -1, null, null, 2]; double?[] ys = [4, null, 6, null, 7];
        for (var index = 0; index < xs.Length; index++)
        {
            var values = new Dictionary<ParameterId, Quantity>();
            if (xs[index] is { } x) values[new("x")] = new(x, PhysicalUnit.Volt);
            if (ys[index] is { } y) values[new("y")] = new(y, PhysicalUnit.Ampere);
            var row = new MeasurementDataRow(data.StartedAt.AddSeconds(index + 1), values); data.Append(row); await archive.OnRowAsync(data.Id, row, CancellationToken.None);
        }
        data.Complete(MeasurementRunState.Completed, data.StartedAt.AddSeconds(6)); await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None); return data.Id;
    }
}

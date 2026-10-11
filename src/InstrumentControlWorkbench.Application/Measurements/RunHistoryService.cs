using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

// Application boundary for desktop history/exports. Browsing storage never
// claims devices, resumes experiments or infers physical safety from old data.
public sealed class RunHistoryService(IRunArchive archive) : IAsyncDisposable
{
    public Task<RunArchiveCatalog> RefreshAsync(CancellationToken token) => archive.ListAsync(token);
    public Task<RunArchiveCatalogPage> BrowseAsync(RunArchiveCatalogQuery query, CancellationToken token) => archive.BrowseAsync(query, token);
    public Task<RunArchiveMetadata> ReadMetadataAsync(RunId id, CancellationToken token) => archive.ReadMetadataAsync(id, token);
    public Task<ArchivedRunPage> ReadPageAsync(RunId id, RunArchiveRowQuery query, CancellationToken token) => archive.ReadPageAsync(id, query, token);
    public async Task<HistoryPlotPage> ReadPlotPageAsync(RunId id, RunArchiveRowQuery query,
        ParameterId xColumn, ParameterId yColumn, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(xColumn); ArgumentNullException.ThrowIfNull(yColumn);
        var page = await archive.ReadPageAsync(id, query, token);
        token.ThrowIfCancellationRequested();
        var columns = page.Metadata.Header.Schema.Columns;
        var x = columns.FirstOrDefault(c => c.Parameter.Id == xColumn)?.Parameter
            ?? throw new ArgumentException($"Unknown X column {xColumn} in the archived schema.", nameof(xColumn));
        var y = columns.FirstOrDefault(c => c.Parameter.Id == yColumn)?.Parameter
            ?? throw new ArgumentException($"Unknown Y column {yColumn} in the archived schema.", nameof(yColumn));
        var samples = new HistoryPlotSample[page.Rows.Count];
        for (var index = 0; index < page.Rows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = page.Rows[index];
            samples[index] = new(page.FirstRowIndex + index, row.Timestamp,
                row.Values.TryGetValue(xColumn, out var xv) ? xv : null,
                row.Values.TryGetValue(yColumn, out var yv) ? yv : null,
                row.MeasurementGroup is not null ? HistoryPlotRowKind.MeasurementGroup :
                row.Analysis is not null ? HistoryPlotRowKind.SampleAnalysis :
                row.Routing is not null ? HistoryPlotRowKind.RoutedReading : HistoryPlotRowKind.Reading,
                row.QualityState, row.QualityIssues, row.ObservedQualityColumns);
        }
        return new(page.Metadata, x, y, Array.AsReadOnly(samples), page.Offset, page.ObservedRowCount,
            page.NextOffset, page.HasIncompleteTail, page.Version, page.ReturnedDataBytes);
    }
    public Task<ArchivedRun> OpenAsync(RunId id, CancellationToken token, int maximumRows = 100_000)
        => archive.LoadAsync(id, token, maximumRows);
    public Task ExportAsync(RunId id, string path, CancellationToken token) => archive.ExportCsvAsync(id, path, token);
    public ValueTask DisposeAsync() => archive.DisposeAsync();
}

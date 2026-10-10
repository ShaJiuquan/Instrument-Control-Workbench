using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Application.Measurements;

// Application boundary for desktop history/exports. Browsing storage never
// claims devices, resumes experiments or infers physical safety from old data.
public sealed class RunHistoryService(IRunArchive archive) : IAsyncDisposable
{
    public Task<RunArchiveCatalog> RefreshAsync(CancellationToken token) => archive.ListAsync(token);
    public Task<ArchivedRun> OpenAsync(RunId id, CancellationToken token, int maximumRows = 100_000)
        => archive.LoadAsync(id, token, maximumRows);
    public Task ExportAsync(RunId id, string path, CancellationToken token) => archive.ExportCsvAsync(id, path, token);
    public ValueTask DisposeAsync() => archive.DisposeAsync();
}

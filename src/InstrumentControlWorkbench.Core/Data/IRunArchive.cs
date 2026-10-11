namespace InstrumentControlWorkbench.Core.Data;

// Absence of a terminal record means unfinalized, never proof of active hardware
// or successful cleanup. Storage observations do not issue device commands.
public sealed record RunArchiveMetadata(RunDataSetSnapshot Header, int? RowCount, long? DataBytes, string? FinalRecordSha256 = null)
{
    public bool IsFinalized => Header.FinishedAt is not null;
}
public sealed record ArchivedRun(RunDataSetSnapshot DataSet, bool IsFinalized, bool HasIncompleteTail);
public sealed record RunArchiveIssue(RunId Id, string Message);
public sealed record RunArchiveCatalog(IReadOnlyList<RunArchiveMetadata> Runs, IReadOnlyList<RunArchiveIssue> Issues);

public interface IRunArchive : IRunResultListener, IAsyncDisposable
{
    Task<RunArchiveCatalog> ListAsync(CancellationToken token);
    Task<RunArchiveMetadata> ReadMetadataAsync(RunId id, CancellationToken token);
    Task<ArchivedRun> LoadAsync(RunId id, CancellationToken token, int maximumRows = 100_000);
    Task ExportCsvAsync(RunId id, string path, CancellationToken token);
}

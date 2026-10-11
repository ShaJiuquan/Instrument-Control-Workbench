using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Infrastructure.Data;

public sealed partial class FileRunArchive
{
    public async Task<ArchivedRunPage> ReadPageAsync(RunId id, RunArchiveRowQuery query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(id); ArgumentNullException.ThrowIfNull(query);
        if (query.ExpectedVersion is { } owned && owned.Id != id) throw new ArgumentException("Page version belongs to a different run.", nameof(query));
        token.ThrowIfCancellationRequested();
        var metadata = await ReadMetadataAsync(id, token);
        if (query.ExpectedVersion is { } expected && (metadata.HeaderRecordSha256 != expected.HeaderSha256 || metadata.FinalRecordSha256 != expected.FinalSha256))
            throw new IOException("Archive metadata changed between pages; refresh the run before continuing.");
        var rows = new List<MeasurementDataRow>(); var cursor = new ReadCursor(); var index = 0; var bytes = 0; var full = false;
        // Read all evidence, including records outside this page. A summary may
        // depend on earlier raw rows, and terminal integrity covers the whole file.
        await foreach (var row in ReadRowsAsync(metadata, cursor, token))
        {
            if (index++ < query.Offset || full) continue;
            if ((long)bytes + cursor.RowBytes > query.MaximumDataBytes)
            {
                if (rows.Count == 0) throw new IOException("One requested record exceeds the page byte budget; increase the budget before continuing.");
                full = true; continue;
            }
            rows.Add(row); bytes += cursor.RowBytes; full = rows.Count == query.PageSize;
        }
        var version = new RunArchiveReadVersion(id, metadata.HeaderRecordSha256!, cursor.DataBytes, cursor.DataSha256, metadata.FinalRecordSha256);
        if (query.ExpectedVersion is { } previous && version != previous)
            throw new IOException("Archive data changed between pages; refresh the run before continuing.");
        return new(metadata, query.Offset, rows.AsReadOnly(), cursor.RowCount, cursor.HasIncompleteTail, version, bytes);
    }

    public async Task<RunArchiveCatalogPage> BrowseAsync(RunArchiveCatalogQuery query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(query); token.ThrowIfCancellationRequested();
        var selected = new SortedSet<RunArchiveSummary>(Comparer<RunArchiveSummary>.Create((left, right) =>
            CompareCatalogPosition(left.StartedAt, left.Id.Value, right.StartedAt, right.Id.Value)));
        RunArchiveCatalogCursor? excluded = null;
        var issues = new List<RunArchiveIssue>(); long matches = 0, scanned = 0, issueCount = 0, characters = 0, issueCharacters = 0;
        if (!Directory.Exists(directory)) return new([], [], null, 0, 0, 0);
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var guid) || guid == Guid.Empty) continue;
            var id = new RunId(guid); scanned++;
            RunArchiveMetadata metadata;
            try { metadata = await ReadMetadataAsync(id, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                issueCount++;
                if (issues.Count < query.PageSize && issueCharacters + error.Message.Length <= query.MaximumTextCharacters)
                { issues.Add(new(id, error.Message)); issueCharacters += error.Message.Length; }
                continue;
            }
            var header = metadata.Header;
            if (query.FinalizedOnly && !metadata.IsFinalized || query.State is { } state && header.State != state ||
                query.SampleId is { } sample && header.Metadata?.SampleId != sample || query.NameContains is { } name && !header.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                query.StartedFrom is { } from && header.StartedAt < from || query.StartedBefore is { } before && header.StartedAt >= before ||
                query.After is { } after && CompareCatalogPosition(header.StartedAt, id.Value, after.StartedAt, after.Id.Value) <= 0) continue;
            matches++;
            // Never fill behind a record excluded by the byte/text budget:
            // doing so could skip an intervening run on the next cursor page.
            if (excluded is { } cutoff && CompareCatalogPosition(header.StartedAt, id.Value, cutoff.StartedAt, cutoff.Id.Value) >= 0) continue;
            var summary = new RunArchiveSummary(id, header.Name, header.StartedAt, header.FinishedAt, header.State, metadata.RowCount, metadata.DataBytes,
                header.Metadata?.SampleId, header.Metadata?.SampleName, header.Instruments.Count, header.Plan is not null, header.Blackboard?.Entries.Count);
            if (selected.Add(summary)) characters += summary.TextCharacters;
            // Keep the newest prefix. A single large leading name is retained
            // until the scan ends, then rejected rather than silently skipped.
            while (selected.Count > query.PageSize || selected.Count > 1 && characters > query.MaximumTextCharacters)
            {
                var last = selected.Max!; selected.Remove(last); characters -= last.TextCharacters;
                if (excluded is null || CompareCatalogPosition(last.StartedAt, last.Id.Value, excluded.StartedAt, excluded.Id.Value) < 0)
                    excluded = new(last.StartedAt, last.Id);
            }
        }
        if (characters > query.MaximumTextCharacters) throw new IOException("The first matching summary exceeds the text budget; increase the budget before continuing.");
        var runs = Array.AsReadOnly(selected.ToArray());
        var next = matches > runs.Count && runs.Count > 0 ? new RunArchiveCatalogCursor(runs[^1].StartedAt, runs[^1].Id) : null;
        return new(runs, issues.AsReadOnly(), next, matches, scanned, issueCount);
    }
    private static int CompareCatalogPosition(DateTimeOffset leftTime, Guid leftId, DateTimeOffset rightTime, Guid rightId)
    {
        var time = rightTime.CompareTo(leftTime);
        return time != 0 ? time : rightId.CompareTo(leftId);
    }
}

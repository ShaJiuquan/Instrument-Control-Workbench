using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Infrastructure.Data;

public sealed partial class FileRunArchive
{
    public async Task<RunArchiveCatalog> ListAsync(CancellationToken token)
    {
        var runs = new List<RunArchiveMetadata>();
        var issues = new List<RunArchiveIssue>();
        if (!Directory.Exists(directory)) return new(runs.AsReadOnly(), issues.AsReadOnly());
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var guid) || guid == Guid.Empty) continue;
            var id = new RunId(guid);
            try { runs.Add(await ReadMetadataAsync(id, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { issues.Add(new(id, ex.Message)); }
        }
        return new(Array.AsReadOnly(runs.OrderByDescending(r => r.Header.StartedAt).ToArray()), issues.AsReadOnly());
    }

    public async Task<RunArchiveMetadata> ReadMetadataAsync(RunId id, CancellationToken token)
    {
        var folder = Folder(id);
        var headerDto = RunArchiveJson.Decode<RunArchiveJson.HeaderDto>(await ReadBoundedAsync(Path.Combine(folder, "metadata.json"), token));
        var header = headerDto.ToDomain();
        if (header.Id != id) throw new FormatException("Archive folder and header run IDs differ.");
        var finalPath = Path.Combine(folder, "final.json");
        if (!File.Exists(finalPath)) return new(header, null, null);
        var finalBytes = await ReadBoundedAsync(finalPath, token);
        var final = RunArchiveJson.Decode<RunArchiveJson.FinalDto>(finalBytes);
        if (final.Format != RunArchiveJson.Format || final.Version != headerDto.Version || final.Id != id.Value ||
            final.State is not (MeasurementRunState.Completed or MeasurementRunState.Aborted or MeasurementRunState.Faulted) ||
            final.FinishedAt < header.StartedAt || final.RowCount < 0 || final.DataBytes < 0 ||
            final.Sha256 is null || final.Sha256.Length != 64 || final.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new FormatException("Invalid terminal archive record.");
        if (final.Version == 1 && (final.BlackboardStatus is not null || final.Blackboard is not null || final.BlackboardSha256 is not null) ||
            final.Version == 2 && (final.BlackboardStatus is null || (final.BlackboardStatus == RunArchiveJson.BlackboardCaptureStatus.Captured) != (final.Blackboard is not null)))
            throw new FormatException("Terminal blackboard capture status differs from its archive version or payload.");
        if ((final.Blackboard is null) != (final.BlackboardSha256 is null)) throw new FormatException("Blackboard snapshot and checksum must be present together.");
        RunBlackboardSnapshot? blackboard = null;
        if (final.Blackboard is { } dto)
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(RunArchiveJson.Encode(dto))), final.BlackboardSha256, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Blackboard snapshot checksum is invalid.");
            try
            {
                blackboard = dto.ToDomain();
                new RunDataRowValidator(header.Plan, header.Schema).ValidateBlackboard(id, blackboard);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { throw new FormatException("Blackboard snapshot provenance is invalid.", error); }
        }
        return new(header with { State = final.State, FinishedAt = final.FinishedAt, Blackboard = blackboard }, final.RowCount, final.DataBytes, Convert.ToHexString(SHA256.HashData(finalBytes)));
    }

    public async Task<ArchivedRun> LoadAsync(RunId id, CancellationToken token, int maximumRows = 100_000)
    {
        if (maximumRows is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        var metadata = await ReadMetadataAsync(id, token);
        if (metadata.RowCount > maximumRows) throw new IOException("Run exceeds the requested in-memory row limit; use streaming export.");
        var rows = new List<MeasurementDataRow>();
        var cursor = new ReadCursor();
        await foreach (var row in ReadRowsAsync(metadata, cursor, token))
        {
            if (rows.Count >= maximumRows) throw new IOException("Run exceeds the requested in-memory row limit; use streaming export.");
            rows.Add(row);
        }
        return new(metadata.Header with { Rows = rows.AsReadOnly() }, metadata.IsFinalized, cursor.HasIncompleteTail);
    }

    private async IAsyncEnumerable<MeasurementDataRow> ReadRowsAsync(RunArchiveMetadata metadata, ReadCursor cursor,
        [EnumeratorCancellation] CancellationToken token)
    {
        var folder = Folder(metadata.Header.Id);
        await using var stream = new FileStream(Path.Combine(folder, "rows.ndjson"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 8192, FileOptions.Asynchronous);
        var boundary = stream.Length;
        if (metadata.DataBytes is { } expectedBytes && boundary != expectedBytes) throw new FormatException("Finalized archive data length differs from its terminal record.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var frame = new MemoryStream();
        var buffer = new byte[8192];
        var count = 0;
        var analysis = new RunDataRowValidator(metadata.Header.Plan, metadata.Header.Schema);
        long read = 0;
        while (read < boundary)
        {
            var length = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, boundary - read)), token);
            if (length == 0) throw new IOException("Archive changed while it was being read.");
            hash.AppendData(buffer.AsSpan(0, length)); read += length;
            var start = 0;
            for (var i = 0; i < length; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                frame.Write(buffer, start, i - start);
                if (frame.Length > RunArchiveJson.MaximumFrameBytes) throw new FormatException("Archive row exceeds 8 MiB.");
                var dto = RunArchiveJson.Decode<RunArchiveJson.RowDto>(frame.GetBuffer().AsSpan(0, (int)frame.Length));
                if (dto.Index != ++count) throw new FormatException("Archive row sequence is missing or duplicated.");
                var row = dto.ToDomain();
                metadata.Header.Schema.ValidateRow(row);
                if (row.Routing is { } context)
                    (metadata.Header.Plan?.Routing ?? throw new FormatException("Routed archive row has no plan routing configuration.")).ValidateContext(context);
                analysis.Append(row);
                frame.SetLength(0); frame.Position = 0; start = i + 1;
                yield return row;
            }
            frame.Write(buffer, start, length - start);
            if (frame.Length > RunArchiveJson.MaximumFrameBytes) throw new FormatException("Archive row exceeds 8 MiB.");
        }
        cursor.HasIncompleteTail = frame.Length > 0;
        if (metadata.IsFinalized)
        {
            var finalBytes = await ReadBoundedAsync(Path.Combine(folder, "final.json"), token);
            if (metadata.FinalRecordSha256 is { } expectedFinal && Convert.ToHexString(SHA256.HashData(finalBytes)) != expectedFinal)
                throw new FormatException("Terminal archive record changed while reading rows.");
            var final = RunArchiveJson.Decode<RunArchiveJson.FinalDto>(finalBytes);
            if (cursor.HasIncompleteTail || count != metadata.RowCount ||
                !string.Equals(Convert.ToHexString(hash.GetCurrentHash()), final.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Finalized archive row count or checksum is invalid.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > RunArchiveJson.MaximumFrameBytes) throw new IOException("Archive metadata exceeds 8 MiB.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > RunArchiveJson.MaximumFrameBytes) throw new IOException("Archive metadata exceeds 8 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return bytes.ToArray();
    }
    private sealed class ReadCursor { public bool HasIncompleteTail { get; set; } }
}

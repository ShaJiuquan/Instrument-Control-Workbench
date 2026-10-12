using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.Infrastructure.Data;

public sealed partial class FileRunArchive : IRunArchive, IRunRowEncoding, IAsyncDisposable
{
    public string RowEncodingId => "instrument-workbench-run-row-v2";
    public byte[] EncodeRow(int index, MeasurementDataRow row) => Writer.RowBytes(index, row);
    private readonly string directory;
    private readonly ConcurrentDictionary<RunId, Writer> active = new();
    private readonly SemaphoreSlim lifetimeGate = new(1, 1);
    private bool disposed;

    public FileRunArchive(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    public async Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(dataSet);
        if (dataSet.State != MeasurementRunState.Running || dataSet.FinishedAt is not null || dataSet.Rows.Count != 0 || dataSet.TotalRowCount != 0 || dataSet.RowOffset != 0 || dataSet.Blackboard is not null)
            throw new ArgumentException("An archive starts with an empty acquiring data set.", nameof(dataSet));
        var bytes = RunArchiveJson.Encode(RunArchiveJson.HeaderDto.From(dataSet));
        var analysis = new RunDataRowValidator(dataSet.Plan, dataSet.Schema);
        await lifetimeGate.WaitAsync(token);
        try
        {
            RequireActive(); token.ThrowIfCancellationRequested();
            var folder = Folder(dataSet.Id);
            if (Directory.Exists(folder)) throw new IOException($"Run {dataSet.Id} already has an archive; it will not be overwritten.");
            Directory.CreateDirectory(folder);
            await WriteAtomicAsync(Path.Combine(folder, "metadata.json"), bytes, token);
            var stream = new FileStream(Path.Combine(folder, "rows.ndjson"), FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 8192, FileOptions.Asynchronous);
            var writer = new Writer(stream, bytes, dataSet.Schema, dataSet.Plan, analysis);
            if (!active.TryAdd(dataSet.Id, writer))
            {
                await writer.DisposeAsync();
                throw new InvalidOperationException("The archive stream already started.");
            }
        }
        finally { lifetimeGate.Release(); }
    }

    public async Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token)
    {
        var writer = GetWriter(id);
        await writer.Gate.WaitAsync(token);
        try
        {
            writer.RequireOpen();
            writer.Schema.ValidateRow(row);
            if (writer.UnconfirmedTail) throw new IOException("A previous archive write was interrupted; finalization must reconcile it first.");
            await writer.AppendAsync(row, token);
        }
        finally { writer.Gate.Release(); }
    }

    public async Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token)
    {
        var writer = GetWriter(dataSet.Id);
        await writer.Gate.WaitAsync(token);
        Exception? error = null;
        try
        {
            writer.RequireOpen();
            if (dataSet.FinishedAt is null || dataSet.State is not (MeasurementRunState.Completed or MeasurementRunState.Aborted or MeasurementRunState.Faulted))
                throw new ArgumentException("A terminal acquisition snapshot is required.", nameof(dataSet));
            if (dataSet.FinishedAt < dataSet.StartedAt || dataSet.TotalRowCount < writer.Count ||
                !RunArchiveJson.Encode(RunArchiveJson.HeaderDto.From(dataSet)).AsSpan().SequenceEqual(writer.Header))
                throw new InvalidOperationException("Final snapshot provenance does not match the started archive.");
            if (dataSet.Blackboard is { } blackboard) writer.Analysis.ValidateBlackboard(dataSet.Id, blackboard);
            if (dataSet.RowWindow is { } window)
            {
                if (window.EncodingId != RowEncodingId || window.RowOffset + dataSet.Rows.Count != window.TotalRows || writer.Count < window.RowOffset) throw new InvalidOperationException("Final live window cannot reconcile this archive prefix.");
                var chain = window.PrefixSha256;
                if (writer.Count == window.RowOffset && !string.Equals(chain, writer.ChainHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Final window changes the committed prefix.");
                for (var i = 0; i < dataSet.Rows.Count; i++)
                {
                    var index = window.RowOffset + i + 1; chain = RunRowWindowProof.AppendHash(chain, Writer.RowBytes(index, dataSet.Rows[i]));
                    if (index == writer.Count && !string.Equals(chain, writer.ChainHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Final window changes previously delivered rows.");
                }
                if (!string.Equals(chain, window.FullSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Final row-window chain is inconsistent.");
            }
            else
            {
                if (!dataSet.HasCompleteRows) throw new InvalidOperationException("A partial preview cannot finalize an archive without canonical row evidence.");
                // Compatibility for callers providing complete immutable results.
                using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var i = 0; i < writer.Count; i++) prefix.AppendData(Writer.RowBytes(i + 1, dataSet.Rows[i]));
                if (!prefix.GetCurrentHash().AsSpan().SequenceEqual(writer.Hash.GetCurrentHash())) throw new InvalidOperationException("Final snapshot changes previously delivered rows.");
            }
            if (writer.UnconfirmedTail)
            {
                await writer.Stream.FlushAsync(token);
                writer.Stream.SetLength(writer.Bytes);
                writer.Stream.Position = writer.Bytes;
                writer.UnconfirmedTail = false;
            }
            // A cancelled external listener can leave a committed application row
            // undelivered. Safe-stop has already happened; persist missing rows now.
            foreach (var row in dataSet.Rows.Skip(writer.Count - dataSet.RowOffset)) await writer.AppendAsync(row, token);
            await writer.Stream.FlushAsync(token);
            var blackboardDto = dataSet.Blackboard is { } snapshot ? RunArchiveJson.BlackboardDto.From(snapshot) : null;
            var blackboardHash = blackboardDto is null ? null : Convert.ToHexString(SHA256.HashData(RunArchiveJson.Encode(blackboardDto)));
            var final = new RunArchiveJson.FinalDto(RunArchiveJson.Format, RunArchiveJson.Version, dataSet.Id.Value,
                dataSet.State, dataSet.FinishedAt.Value, writer.Count, writer.Bytes, Convert.ToHexString(writer.Hash.GetCurrentHash()), blackboardDto, blackboardHash, blackboardDto is null ? RunArchiveJson.BlackboardCaptureStatus.NotCaptured : RunArchiveJson.BlackboardCaptureStatus.Captured);
            await WriteAtomicAsync(Path.Combine(Folder(dataSet.Id), "final.json"), RunArchiveJson.Encode(final), token);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            writer.Closed = true;
            active.TryRemove(dataSet.Id, out _);
            try { await writer.DisposeAsync(); }
            catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
            writer.Gate.Release();
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        await lifetimeGate.WaitAsync(CancellationToken.None);
        try
        {
            if (disposed) return;
            disposed = true;
            var errors = new List<Exception>();
            foreach (var pair in active.ToArray())
            {
                var writer = pair.Value;
                await writer.Gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (writer.Closed) continue;
                    writer.Closed = true;
                    active.TryRemove(pair.Key, out _);
                    try { await writer.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
                }
                finally { writer.Gate.Release(); }
            }
            // Disposal never invents a terminal outcome for an unfinished run.
            if (errors.Count > 0) throw new AggregateException("Archive close failed.", errors);
        }
        finally { lifetimeGate.Release(); }
    }

    private Writer GetWriter(RunId id) => active.TryGetValue(id, out var writer) ? writer
        : throw new InvalidOperationException($"Run {id} has no open archive stream.");
    private string Folder(RunId id) { ArgumentNullException.ThrowIfNull(id); return Path.Combine(directory, id.Value.ToString("N")); }
    private void RequireActive() { if (disposed) throw new ObjectDisposedException(nameof(FileRunArchive)); }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        Exception? error = null;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class Writer(FileStream stream, byte[] header, DataSetSchema schema, ExperimentPlan? plan, RunDataRowValidator analysis) : IAsyncDisposable
    {
        public RunDataRowValidator Analysis { get; } = analysis;
        public FileStream Stream { get; } = stream;
        public byte[] Header { get; } = header;
        public DataSetSchema Schema { get; } = schema;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string ChainHash { get; private set; } = RunRowWindowProof.EmptyHash;
        public int Count { get; private set; }
        public long Bytes { get; private set; }
        public bool UnconfirmedTail { get; set; }
        public bool Closed { get; set; }
        public void RequireOpen() { if (Closed) throw new InvalidOperationException("The archive stream is closed."); }
        public static byte[] RowBytes(int index, MeasurementDataRow row)
        {
            var bytes = RunArchiveJson.Encode(RunArchiveJson.RowDto.From(index, row));
            var line = new byte[bytes.Length + 1];
            bytes.CopyTo(line, 0); line[^1] = (byte)'\n';
            return line;
        }
        public async Task AppendAsync(MeasurementDataRow row, CancellationToken token)
        {
            Schema.ValidateRow(row);
            if (row.Routing is { } context)
                (plan?.Routing ?? throw new ArgumentException("Routed archive rows require the frozen plan routing configuration.", nameof(row))).ValidateContext(context);
            Analysis.Validate(row);
            var bytes = RowBytes(Count + 1, row);
            UnconfirmedTail = true;
            await Stream.WriteAsync(bytes, token);
            await Stream.FlushAsync(token);
            Analysis.Append(row);
            Hash.AppendData(bytes); ChainHash = RunRowWindowProof.AppendHash(ChainHash, bytes);
            Bytes += bytes.Length; Count++; UnconfirmedTail = false;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync(); } finally { Hash.Dispose(); }
        }
    }
}

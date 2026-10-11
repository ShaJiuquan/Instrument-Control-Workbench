using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class RunArchiveTests
{
    [Fact]
    public async Task FreshHistoryPagesRetainOnlyRequestedRowsAndUseStableWholeArchiveVersions()
    {
        using var folder = new TemporaryFolder(); RunId id;
        await using (var writer = new FileRunArchive(folder.Path)) id = (await SavePageRunAsync(writer, rows: 25)).Id;
        await using var history = new RunHistoryService(new FileRunArchive(folder.Path));
        await Assert.ThrowsAsync<IOException>(() => history.OpenAsync(id, CancellationToken.None, maximumRows: 2));
        var values = new List<double>(); RunArchiveReadVersion? version = null; var offset = 0;
        do
        {
            var page = await history.ReadPageAsync(id, new(offset, 4, version), CancellationToken.None);
            version ??= page.Version; Assert.Equal(version, page.Version); Assert.Equal(id, page.Version.Id);
            Assert.Equal(25, page.ObservedRowCount); Assert.Empty(page.Metadata.Header.Rows); Assert.InRange(page.Rows.Count, 1, 4);
            Assert.Equal((long)offset + 1, page.FirstRowIndex); Assert.False(page.HasIncompleteTail); Assert.True(page.Metadata.IsFinalized);
            values.AddRange(page.Rows.Select(row => row.Values[new("x")].Value));
            Assert.Throws<NotSupportedException>(() => ((IList<MeasurementDataRow>)page.Rows).Clear());
            if (page.NextOffset is not { } next) break; Assert.True(next > offset); offset = next;
        } while (true);
        Assert.Equal(Enumerable.Range(1, 25).Select(x => (double)x), values);
        var beyond = await history.ReadPageAsync(id, new(int.MaxValue, 1, version), CancellationToken.None);
        Assert.Empty(beyond.Rows); Assert.False(beyond.HasMore); Assert.Null(beyond.NextOffset); Assert.Equal(25, beyond.ObservedRowCount);
        Assert.Equal(version!.HeaderSha256, (await history.ReadMetadataAsync(id, CancellationToken.None)).HeaderRecordSha256);
    }

    [Fact]
    public async Task RecordByteBudgetReturnsAContiguousPrefixWithoutSkippingAnOversizedNextRow()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        var data = await SavePageRunAsync(archive, rows: 5);
        var records = await File.ReadAllLinesAsync(System.IO.Path.Combine(folder.Path, data.Id.Value.ToString("N"), "rows.ndjson"));
        var budget = Encoding.UTF8.GetByteCount(records[0]) + Encoding.UTF8.GetByteCount(records[1]) + 2;
        var page = await archive.ReadPageAsync(data.Id, new(pageSize: 5, maximumDataBytes: budget), CancellationToken.None);
        Assert.Equal(new[] { 1.0, 2.0 }, page.Rows.Select(r => r.Values[new("x")].Value)); Assert.Equal(budget, page.ReturnedDataBytes); Assert.Equal(2, page.NextOffset);
        var next = await archive.ReadPageAsync(data.Id, new(page.NextOffset!.Value, 5, page.Version, budget), CancellationToken.None);
        Assert.Equal(new[] { 3.0, 4.0 }, next.Rows.Select(r => r.Values[new("x")].Value)); Assert.Equal(4, next.NextOffset);
        await Assert.ThrowsAsync<IOException>(() => archive.ReadPageAsync(data.Id, new(maximumDataBytes: 1), CancellationToken.None));
        Assert.Equal(5, (await archive.LoadAsync(data.Id, CancellationToken.None)).DataSet.Rows.Count);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("unit")]
    [InlineData("sequence")]
    [InlineData("duplicate")]
    [InlineData("incomplete")]
    public async Task CorruptionOutsideTheRequestedPageIsRejectedBeforeReturningAnyTrustedRows(string corruption)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var data = await SavePageRunAsync(archive, rows: 5);
        var path = System.IO.Path.Combine(folder.Path, data.Id.Value.ToString("N"), "rows.ndjson"); var lines = await File.ReadAllLinesAsync(path);
        var row = JsonNode.Parse(lines[^1])!;
        if (corruption == "checksum") row["values"]!["x"]!["value"] = 9;
        if (corruption == "unit") row["values"]!["x"]!["unit"] = "Tesla";
        if (corruption == "sequence") row["index"] = 99;
        lines[^1] = corruption == "duplicate" ? lines[^1].Replace("\"index\":5", "\"index\":5,\"index\":5", StringComparison.Ordinal) : row.ToJsonString();
        await File.WriteAllTextAsync(path, string.Join('\n', lines) + (corruption == "incomplete" ? "" : "\n"));
        if (corruption != "checksum")
        {
            // Reconcile the manifest so source/schema/sequence validation must
            // reject independently of the byte-count/checksum guards.
            var finalPath = System.IO.Path.Combine(folder.Path, data.Id.Value.ToString("N"), "final.json"); var final = JsonNode.Parse(await File.ReadAllTextAsync(finalPath))!;
            var bytes = await File.ReadAllBytesAsync(path); final["dataBytes"] = bytes.Length; final["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            await File.WriteAllTextAsync(finalPath, final.ToJsonString());
        }
        Assert.NotNull(await Record.ExceptionAsync(() => archive.ReadPageAsync(data.Id, new(0, 1), CancellationToken.None)));
        Assert.NotNull(await Record.ExceptionAsync(() => archive.ReadPageAsync(data.Id, new(100, 1), CancellationToken.None)));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("final")]
    [InlineData("data")]
    public async Task ExpectedVersionPreventsMixingPagesAfterValidArchiveReplacement(string change)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var data = await SavePageRunAsync(archive, rows: 4);
        var first = await archive.ReadPageAsync(data.Id, new(0, 1), CancellationToken.None); var run = System.IO.Path.Combine(folder.Path, data.Id.Value.ToString("N"));
        if (change == "header")
        {
            var path = System.IO.Path.Combine(run, "metadata.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!; json["name"] = "Changed"; await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        else
        {
            var path = System.IO.Path.Combine(run, "final.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (change == "final") json["state"] = "Aborted";
            else
            {
                var rowsPath = System.IO.Path.Combine(run, "rows.ndjson"); var lines = await File.ReadAllLinesAsync(rowsPath); var row = JsonNode.Parse(lines[0])!; row["values"]!["x"]!["value"] = 9;
                lines[0] = row.ToJsonString(); var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"); await File.WriteAllBytesAsync(rowsPath, bytes);
                json["dataBytes"] = bytes.Length; json["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        await Assert.ThrowsAsync<IOException>(() => archive.ReadPageAsync(data.Id, new(1, 1, first.Version), CancellationToken.None));
        var refreshed = await archive.ReadPageAsync(data.Id, new(1, 1), CancellationToken.None); Assert.NotEqual(first.Version, refreshed.Version);
        Assert.Equal(2, Assert.Single(refreshed.Rows).Values[new("x")].Value); Assert.Equal(1, Assert.Single(first.Rows).Values[new("x")].Value);
    }

    [Fact]
    public async Task UnfinalizedPagesKeepTheirReadBoundaryAndRejectGrowthOrTerminalPromotion()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var data = DataSet();
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None); data.Append(Row(1)); await archive.OnRowAsync(data.Id, data.Rows[0], CancellationToken.None);
        var initial = await archive.ReadPageAsync(data.Id, new(), CancellationToken.None); Assert.False(initial.Metadata.IsFinalized); Assert.Null(initial.Metadata.RowCount); Assert.Null(initial.Version.FinalSha256);
        data.Append(Row(2)); await archive.OnRowAsync(data.Id, data.Rows[1], CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => archive.ReadPageAsync(data.Id, new(1, 1, initial.Version), CancellationToken.None));
        var current = await archive.ReadPageAsync(data.Id, new(1, 1), CancellationToken.None); Assert.Equal(2, current.ObservedRowCount); Assert.Single(current.Rows);
        data.Complete(MeasurementRunState.Aborted, DateTimeOffset.UtcNow); await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => archive.ReadPageAsync(data.Id, new(0, 1, current.Version), CancellationToken.None));
        var complete = await archive.ReadPageAsync(data.Id, new(), CancellationToken.None); Assert.True(complete.Metadata.IsFinalized); Assert.Equal(MeasurementRunState.Aborted, complete.Metadata.Header.State);
        Assert.Single(initial.Rows); Assert.Equal(1, initial.ObservedRowCount); Assert.False(initial.Metadata.IsFinalized);
    }

    [Fact]
    public async Task UnfinishedTailIsExcludedButFingerprintDetectsLaterTailChanges()
    {
        using var folder = new TemporaryFolder(); RunId id;
        await using (var writer = new FileRunArchive(folder.Path)) id = (await SavePageRunAsync(writer, rows: 2, finalState: null)).Id;
        var path = System.IO.Path.Combine(folder.Path, id.Value.ToString("N"), "rows.ndjson"); await File.AppendAllTextAsync(path, "{\"index\":3");
        await using var reader = new FileRunArchive(folder.Path); var page = await reader.ReadPageAsync(id, new(1, 1), CancellationToken.None);
        Assert.True(page.HasIncompleteTail); Assert.Equal(2, page.ObservedRowCount); Assert.Equal(2, Assert.Single(page.Rows).Values[new("x")].Value);
        await File.AppendAllTextAsync(path, ",\"timestamp\":");
        await Assert.ThrowsAsync<IOException>(() => reader.ReadPageAsync(id, new(0, 1, page.Version), CancellationToken.None));
        Assert.True((await reader.ReadPageAsync(id, new(100, 1), CancellationToken.None)).HasIncompleteTail);
    }

    [Fact]
    public async Task QueryCancellationAndForeignVersionsNeverMutateTheSourceArchive()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var first = await SavePageRunAsync(archive); var second = await SavePageRunAsync(archive);
        var version = (await archive.ReadPageAsync(first.Id, new(), CancellationToken.None)).Version;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.ReadPageAsync(first.Id, new(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.BrowseAsync(new(), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => archive.ReadPageAsync(second.Id, new(expectedVersion: version), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => archive.ReadPageAsync(first.Id, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => archive.BrowseAsync(null!, CancellationToken.None));
        Assert.Equal(version, (await archive.ReadPageAsync(first.Id, new(), CancellationToken.None)).Version);
        Assert.Equal(4, (await archive.LoadAsync(second.Id, CancellationToken.None)).DataSet.Rows.Count);
    }

    [Fact]
    public async Task SameLengthUnfinalizedRewriteIsDetectedByDataFingerprint()
    {
        using var folder = new TemporaryFolder(); RunId id;
        await using (var writer = new FileRunArchive(folder.Path)) id = (await SavePageRunAsync(writer, rows: 2, finalState: null)).Id;
        await using var reader = new FileRunArchive(folder.Path); var page = await reader.ReadPageAsync(id, new(0, 1), CancellationToken.None);
        var path = System.IO.Path.Combine(folder.Path, id.Value.ToString("N"), "rows.ndjson"); var bytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(bytes).Replace("\"value\":1", "\"value\":9", StringComparison.Ordinal); var replacement = Encoding.UTF8.GetBytes(text);
        Assert.Equal(bytes.Length, replacement.Length); Assert.NotEqual(bytes, replacement); await File.WriteAllBytesAsync(path, replacement);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadPageAsync(id, new(1, 1, page.Version), CancellationToken.None));
        var fresh = await reader.ReadPageAsync(id, new(0, 1), CancellationToken.None); Assert.Equal(9, Assert.Single(fresh.Rows).Values[new("x")].Value); Assert.NotEqual(page.Version.DataSha256, fresh.Version.DataSha256);
        Assert.Equal(page.Version.HeaderSha256, fresh.Version.HeaderSha256); Assert.Equal(page.Version.DataBytes, fresh.Version.DataBytes); Assert.Null(fresh.Version.FinalSha256);
    }

    [Fact]
    public async Task LegacyVersionOnePagesRemainReadableWithUnknownBlackboard()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var data = await SavePageRunAsync(archive, rows: 3);
        var run = System.IO.Path.Combine(folder.Path, data.Id.Value.ToString("N"));
        foreach (var name in new[] { "metadata.json", "final.json" })
        {
            var path = System.IO.Path.Combine(run, name); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!; json["version"] = 1;
            if (name == "final.json") { json.AsObject().Remove("blackboardStatus"); json.AsObject().Remove("blackboard"); json.AsObject().Remove("blackboardSha256"); }
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        var page = await archive.ReadPageAsync(data.Id, new(1, 1), CancellationToken.None); Assert.Equal(2, Assert.Single(page.Rows).Values[new("x")].Value); Assert.Null(page.Metadata.Header.Blackboard);
        var summary = Assert.Single((await archive.BrowseAsync(new(), CancellationToken.None)).Runs); Assert.Null(summary.BlackboardEntryCount); Assert.True(summary.IsFinalized);
    }

    [Fact]
    public async Task LargeRestartedArchiveCanBePagedBeyondTheDefaultFullLoadLimit()
    {
        using var folder = new TemporaryFolder(); var header = DataSet();
        // Feed the storage port directly; neither the fixture nor the archive
        // writer retains a full in-memory dataset for these synthetic rows.
        await using (var writer = new FileRunArchive(folder.Path))
        {
            await writer.OnStartedAsync(header.Capture(), CancellationToken.None);
            for (var i = 1; i <= 100_005; i++) await writer.OnRowAsync(header.Id, Row(i), CancellationToken.None);
        }
        await using var history = new RunHistoryService(new FileRunArchive(folder.Path));
        await Assert.ThrowsAsync<IOException>(() => history.OpenAsync(header.Id, CancellationToken.None));
        var page = await history.ReadPageAsync(header.Id, new(100_000, 3), CancellationToken.None);
        Assert.Equal(100_005, page.ObservedRowCount); Assert.Equal(new[] { 100_001.0, 100_002.0, 100_003.0 }, page.Rows.Select(r => r.Values[new("x")].Value));
        Assert.Equal(100_003, page.NextOffset); Assert.Empty(page.Metadata.Header.Rows); Assert.False(page.Metadata.IsFinalized); Assert.False(page.HasIncompleteTail);
        var last = await history.ReadPageAsync(header.Id, new(page.NextOffset!.Value, 3, page.Version), CancellationToken.None);
        Assert.Equal(new[] { 100_004.0, 100_005.0 }, last.Rows.Select(r => r.Values[new("x")].Value)); Assert.False(last.HasMore); Assert.Null(last.NextOffset);
        Assert.Equal(page.Version, last.Version); Assert.Empty(header.Rows);
    }

    [Fact]
    public async Task WideUtf8PagesRespectSourceByteBudgetAcrossMultipleReadBuffers()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        var columns = Enumerable.Range(0, 300).Select(i => new ParameterDescriptor(new("电压_" + i), "Voltage " + i, PhysicalUnit.Volt, ParameterAccess.Read)).ToArray();
        var data = new RunDataSet(RunId.New(), "wide UTF8", DateTimeOffset.UtcNow, new(columns.Select(p => new DataColumn(p))), new Dictionary<string, Quantity>(), []);
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        for (var i = 1; i <= 3; i++)
        {
            var row = new MeasurementDataRow(DateTimeOffset.UnixEpoch, columns.ToDictionary(p => p.Id, _ => new Quantity(i + 0.125, PhysicalUnit.Volt)));
            data.Append(row); await archive.OnRowAsync(data.Id, row, CancellationToken.None);
        }
        data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow); await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None);
        var line = (await File.ReadAllLinesAsync(System.IO.Path.Combine(folder.Path, data.Id.Value.ToString("N"), "rows.ndjson")))[0]; var budget = Encoding.UTF8.GetByteCount(line) + 1;
        Assert.True(budget > 8192); Assert.True(budget > line.Length + 1);
        var page = await archive.ReadPageAsync(data.Id, new(pageSize: 3, maximumDataBytes: budget), CancellationToken.None);
        Assert.Equal(budget, page.ReturnedDataBytes); Assert.Equal(1, page.NextOffset); Assert.Equal(300, Assert.Single(page.Rows).Values.Count);
        Assert.Equal(1.125, page.Rows[0].Values[new("电压_299")].Value); Assert.Equal(PhysicalUnit.Volt, page.Rows[0].Values[new("电压_299")].Unit);
        var next = await archive.ReadPageAsync(data.Id, new(1, 3, page.Version, budget), CancellationToken.None); Assert.Equal(2.125, Assert.Single(next.Rows).Values[new("电压_299")].Value);
    }

    private static async Task<RunDataSet> SavePageRunAsync(FileRunArchive archive, int rows = 4, string name = "paged", DateTimeOffset? startedAt = null,
        MeasurementRunState? finalState = MeasurementRunState.Completed, RunMetadata? metadata = null)
    {
        var data = new RunDataSet(RunId.New(), name, startedAt ?? DateTimeOffset.UtcNow,
            new([new(new ParameterDescriptor(new("x"), "Voltage", PhysicalUnit.Volt, ParameterAccess.Read))]), new Dictionary<string, Quantity>(), [], metadata: metadata);
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        for (var index = 1; index <= rows; index++) { var row = Row(index); data.Append(row); await archive.OnRowAsync(data.Id, row, CancellationToken.None); }
        if (finalState is { } state) { data.Complete(state, data.StartedAt.AddSeconds(1)); await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None); }
        return data;
    }
}

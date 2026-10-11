using System.Globalization;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class RunArchiveTests
{
    [Fact]
    public async Task CatalogKeysetPagesHaveDeterministicTimestampAndIdOrderWithoutDuplicates()
    {
        using var folder = new TemporaryFolder(); await using var writer = new FileRunArchive(folder.Path);
        var start = DateTimeOffset.UnixEpoch; var data = new List<RunDataSet>();
        for (var i = 0; i < 9; i++) data.Add(await SavePageRunAsync(writer, rows: 0, name: "Run " + i, startedAt: start.AddSeconds(i / 3)));
        await using var history = new RunHistoryService(new FileRunArchive(folder.Path));
        var expected = data.OrderByDescending(d => d.StartedAt).ThenByDescending(d => d.Id.Value).Select(d => d.Id).ToArray();
        var ids = new List<RunId>(); RunArchiveCatalogCursor? after = null;
        do
        {
            var page = await history.BrowseAsync(new(pageSize: 2, after: after), CancellationToken.None); Assert.Empty(page.Issues);
            Assert.Equal(9, page.ScannedRuns); Assert.InRange(page.Runs.Count, 1, 2); Assert.Equal(9 - ids.Count, page.MatchingRuns);
            ids.AddRange(page.Runs.Select(r => r.Id)); Assert.Throws<NotSupportedException>(() => ((IList<RunArchiveSummary>)page.Runs).Clear());
            after = page.NextCursor; if (after is null) break; Assert.Equal(page.Runs[^1].Id, after.Id);
        } while (true);
        Assert.Equal(expected, ids); Assert.Equal(9, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TextBudgetKeepsSortedPrefixesAcrossArbitraryDirectoryCreationOrder(int seed)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        var random = new Random(seed); var order = Enumerable.Range(0, 24).OrderBy(_ => random.Next()).ToArray(); var data = new List<RunDataSet>();
        foreach (var i in order) data.Add(await SavePageRunAsync(archive, rows: 0, name: new string('x', i % 3 == 0 ? 19 : i % 3 == 1 ? 10 : 1), startedAt: DateTimeOffset.UnixEpoch.AddSeconds(i)));
        var sorted = data.OrderByDescending(d => d.StartedAt).ToArray(); var seen = 0; RunArchiveCatalogCursor? after = null;
        while (seen < sorted.Length)
        {
            var page = await archive.BrowseAsync(new(pageSize: 11, after: after, maximumTextCharacters: 20), CancellationToken.None);
            var expected = new List<RunId>(); var chars = 0;
            foreach (var item in sorted.Skip(seen)) { if (expected.Count == 11 || chars + item.Name.Length > 20) break; expected.Add(item.Id); chars += item.Name.Length; }
            Assert.NotEmpty(expected); Assert.Equal(expected, page.Runs.Select(r => r.Id)); Assert.InRange(page.Runs.Sum(r => r.TextCharacters), 1, 20);
            seen += page.Runs.Count; Assert.Equal(seen < sorted.Length, page.HasMore); after = page.NextCursor;
        }
        Assert.Equal(sorted.Length, seen);
    }

    [Fact]
    public async Task OversizedFirstSummaryIsRejectedRatherThanSkippedOrTruncated()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        await SavePageRunAsync(archive, rows: 0, name: "old", startedAt: DateTimeOffset.UnixEpoch);
        var newest = await SavePageRunAsync(archive, rows: 0, name: new string('x', 30), startedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => archive.BrowseAsync(new(maximumTextCharacters: 20), CancellationToken.None));
        var page = await archive.BrowseAsync(new(maximumTextCharacters: 40), CancellationToken.None); Assert.Equal(newest.Id, page.Runs[0].Id); Assert.Equal(newest.Name, page.Runs[0].Name);
    }

    [Fact]
    public async Task FiltersUseSampleIdentityOrdinalNameSearchAndInclusiveExclusiveTimeBounds()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var origin = DateTimeOffset.UnixEpoch;
        var wanted = await SavePageRunAsync(archive, rows: 0, name: "IV 硅片 A", startedAt: origin.AddSeconds(1), metadata: new(new("sample"), "Si"));
        await SavePageRunAsync(archive, rows: 0, name: "IV 硅片 B", startedAt: origin.AddSeconds(2), metadata: new(new("sample"), "Si"), finalState: MeasurementRunState.Aborted);
        await SavePageRunAsync(archive, rows: 0, name: "IV 硅片 C", startedAt: origin.AddSeconds(3), metadata: new(new("Sample"), "Ge"));
        await SavePageRunAsync(archive, rows: 0, name: "IV 硅片 unfinished", startedAt: origin.AddSeconds(4), metadata: new(new("sample")), finalState: null);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var query = new RunArchiveCatalogQuery(nameContains: "iv 硅片", sampleId: new("sample"), startedFrom: wanted.StartedAt,
                startedBefore: origin.AddSeconds(3), state: MeasurementRunState.Completed, finalizedOnly: true);
            var page = await archive.BrowseAsync(query, CancellationToken.None); Assert.Equal(wanted.Id, Assert.Single(page.Runs).Id); Assert.Equal(1, page.MatchingRuns);
            var unfinished = await archive.BrowseAsync(new(state: MeasurementRunState.Running), CancellationToken.None); Assert.False(Assert.Single(unfinished.Runs).IsFinalized); Assert.Null(unfinished.Runs[0].RowCount);
            Assert.Empty((await archive.BrowseAsync(new(startedFrom: wanted.StartedAt, startedBefore: wanted.StartedAt), CancellationToken.None)).Runs);
            Assert.Equal(3, (await archive.BrowseAsync(new(finalizedOnly: true), CancellationToken.None)).Runs.Count);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public async Task LaterNewerRunsDoNotShiftExistingKeysetPagesAndRefreshIncludesThem()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var data = new List<RunDataSet>();
        for (var i = 0; i < 4; i++) data.Add(await SavePageRunAsync(archive, rows: 0, startedAt: DateTimeOffset.UnixEpoch.AddSeconds(i)));
        var first = await archive.BrowseAsync(new(pageSize: 2), CancellationToken.None); Assert.NotNull(first.NextCursor);
        var newest = await SavePageRunAsync(archive, rows: 0, startedAt: DateTimeOffset.UnixEpoch.AddSeconds(10));
        var second = await archive.BrowseAsync(new(pageSize: 2, after: first.NextCursor), CancellationToken.None);
        Assert.Equal(new[] { data[1].Id, data[0].Id }, second.Runs.Select(r => r.Id)); Assert.False(second.HasMore);
        Assert.Equal(newest.Id, (await archive.BrowseAsync(new(pageSize: 2), CancellationToken.None)).Runs[0].Id);
    }

    [Fact]
    public async Task CatalogBoundsCorruptionDetailsAndCountsOmittedIssuesExplicitly()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var good = await SavePageRunAsync(archive, rows: 0, name: "K");
        for (var i = 0; i < 10; i++) Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(System.IO.Path.Combine(folder.Path, "not-a-run"));
        var page = await archive.BrowseAsync(new(pageSize: 2), CancellationToken.None);
        Assert.Equal(good.Id, Assert.Single(page.Runs).Id); Assert.Equal(11, page.ScannedRuns); Assert.Equal(10, page.IssueCount); Assert.Equal(2, page.Issues.Count); Assert.True(page.HasOmittedIssues);
        var tiny = await archive.BrowseAsync(new(pageSize: 1, maximumTextCharacters: 1), CancellationToken.None);
        Assert.Single(tiny.Runs); Assert.Empty(tiny.Issues); Assert.Equal(10, tiny.IssueCount); Assert.True(tiny.HasOmittedIssues);
    }

    [Fact]
    public async Task MissingCatalogRemainsEmptyAndStillHonorsCancellation()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(System.IO.Path.Combine(folder.Path, "absent"));
        var page = await archive.BrowseAsync(new(), CancellationToken.None); Assert.Empty(page.Runs); Assert.Equal(0, page.ScannedRuns); Assert.Equal(0, page.IssueCount); Assert.Null(page.NextCursor);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.BrowseAsync(new(), cancellation.Token));
        Assert.False(Directory.Exists(System.IO.Path.Combine(folder.Path, "absent")));
    }
}

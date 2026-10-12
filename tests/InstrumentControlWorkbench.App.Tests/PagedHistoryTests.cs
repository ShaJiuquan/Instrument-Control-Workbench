using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PagedHistoryTests
{
    [Fact]
    public async Task DesktopUsesBoundedDirectoryAndPlotPagesWithSchemaAxesAndSourceGaps()
    {
        using var folder = new TemporaryFolder(); RunId id;
        await using (var writer = new FileRunArchive(folder.Path)) id = await SaveAsync(writer, 505);
        await using var archive = new FileRunArchive(folder.Path); var gate = new ReadGateArchive(archive);
        await using var vm = new PagedRunHistoryViewModel(new(gate));
        Assert.False(vm.OpenCommand.CanExecute(null)); Assert.False(vm.NextPlotCommand.CanExecute(null));
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal(id, vm.SelectedRun!.Id);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Equal("x", vm.XAxis!.Id.Value); Assert.Equal("y", vm.YAxis!.Id.Value);
        Assert.Equal(PhysicalUnit.Kelvin, vm.XAxis.Unit); Assert.Equal(PhysicalUnit.Ohm, vm.YAxis.Unit);
        Assert.Equal(500, vm.Plot!.Samples.Count); Assert.Empty(vm.Plot.Metadata.Header.Rows); Assert.Contains("1–500 / 505", vm.PageSummary);
        Assert.Equal(0, vm.Plot.Samples[0].Y!.Value.Value); Assert.False(vm.Plot.Samples[2].HasCoordinates); Assert.Null(vm.Plot.Samples[2].Y);
        Assert.Contains("缺测 1", vm.QualitySummary); Assert.Contains("未观察质量 500", vm.QualitySummary); Assert.Contains("设备当前状态未知", vm.RecordStatus);
        var firstVersion = vm.Plot.Version;
        await vm.NextPlotCommand.ExecuteAsync(null); Assert.Equal(5, vm.Plot.Samples.Count); Assert.Contains("501–505", vm.PageSummary);
        Assert.Equal(firstVersion, gate.LastQuery!.ExpectedVersion); Assert.Equal("501", vm.StartRowText); Assert.False(vm.NextPlotCommand.CanExecute(null));
        vm.StartRowText = "2"; await vm.JumpCommand.ExecuteAsync(null); Assert.Equal(2, vm.Plot.Samples[0].RowIndex); Assert.Equal(firstVersion, vm.Plot.Version);
        Assert.Equal(0, gate.FullLoadCalls); Assert.Equal(0, gate.FullCatalogCalls);
    }

    [Fact]
    public async Task CatalogPagingReplacesTheVisiblePageAndSearchCannotReuseOldCursor()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        for (var i = 0; i < 51; i++) await SaveAsync(archive, 1, $"run{i:D2}");
        await using var vm = new PagedRunHistoryViewModel(new(archive));
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal(50, vm.Runs.Count); Assert.True(vm.NextCatalogCommand.CanExecute(null));
        var firstIds = vm.Runs.Select(r => r.Id).ToHashSet();
        await vm.NextCatalogCommand.ExecuteAsync(null); Assert.Single(vm.Runs); Assert.DoesNotContain(vm.Runs[0].Id, firstIds); Assert.False(vm.NextCatalogCommand.CanExecute(null));
        vm.SearchText = "run50"; Assert.False(vm.NextCatalogCommand.CanExecute(null)); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("run50", Assert.Single(vm.Runs).Name); Assert.Null(vm.Plot);
        vm.SearchText = "no match"; await vm.RefreshCommand.ExecuteAsync(null); Assert.Empty(vm.Runs); Assert.Null(vm.SelectedRun); Assert.False(vm.OpenCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("+1")]
    [InlineData("2,000")]
    [InlineData("2147483649")]
    [InlineData("999999999999999999999")]
    public async Task InvalidJumpTextCannotReadStorageOrReplaceTrustedPage(string text)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 3);
        var gate = new ReadGateArchive(archive); await using var vm = new PagedRunHistoryViewModel(new(gate));
        await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null); var original = vm.Plot; var reads = gate.PageReadCalls;
        vm.StartRowText = text; Assert.False(vm.JumpCommand.CanExecute(null)); await vm.JumpCommand.ExecuteAsync(null);
        Assert.Same(original, vm.Plot); Assert.Equal(reads, gate.PageReadCalls);
    }

    [Fact]
    public async Task ChangingSelectionDuringCanceledNonCooperativeReadDiscardsOldResultsAndDrainsBeforeNextRead()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 2, "first"); await SaveAsync(archive, 2, "second");
        var gate = new ReadGateArchive(archive); await using var vm = new PagedRunHistoryViewModel(new(gate)); await vm.RefreshCommand.ExecuteAsync(null);
        var other = vm.Runs.Single(r => r.Id != vm.SelectedRun!.Id); gate.Block = true; var read = vm.OpenCommand.ExecuteAsync(null);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(vm.IsBusy); Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null)); await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal(1, gate.BrowseCalls);
        vm.SelectedRun = other; Assert.Null(vm.Plot); Assert.False(vm.OpenCommand.CanExecute(null)); Assert.True(gate.LastToken.IsCancellationRequested);
        gate.Release.SetResult(); await read; Assert.False(vm.IsBusy); Assert.Null(vm.Plot); Assert.Empty(vm.Axes); Assert.Equal(other.Id, vm.SelectedRun!.Id);
        gate.Block = false; await vm.OpenCommand.ExecuteAsync(null); Assert.Equal(other.Id, vm.Plot!.Metadata.Header.Id);
    }

    [Fact]
    public async Task CancelAndAxisChangeDoNotAcceptLatePagesOrDisposeSharedHistory()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 505);
        var gate = new ReadGateArchive(archive); var service = new RunHistoryService(gate); await using var vm = new PagedRunHistoryViewModel(service);
        await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null); var original = vm.Plot;
        gate.Block = true; var read = vm.NextPlotCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelCommand.Execute(null); gate.Release.SetResult(); await read; Assert.Same(original, vm.Plot); Assert.Contains("取消", vm.Message);
        gate.Reset(); read = vm.NextPlotCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.YAxis = vm.XAxis; Assert.Null(vm.Plot); Assert.True(gate.LastToken.IsCancellationRequested); gate.Release.SetResult(); await read; Assert.Null(vm.Plot);
        gate.Block = false; await vm.ReadPlotCommand.ExecuteAsync(null); Assert.Equal(vm.Plot!.XAxis.Id, vm.Plot.YAxis.Id);
        var id = vm.SelectedRun!.Id; await vm.DisposeAsync(); Assert.False(vm.CanRead); Assert.Null(vm.Plot); Assert.Empty(vm.Axes); Assert.Empty(vm.Runs);
        Assert.Equal(0, gate.DisposeCalls); Assert.Equal(id, (await service.ReadMetadataAsync(id, CancellationToken.None)).Header.Id);
    }

    [Fact]
    public async Task ConcurrentDisposeCallsWaitForTheSameLiveReadAndPreventLatePublication()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 2);
        var gate = new ReadGateArchive(archive); var vm = new PagedRunHistoryViewModel(new(gate)); await vm.RefreshCommand.ExecuteAsync(null);
        gate.Block = true; var read = vm.OpenCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose1 = vm.DisposeAsync().AsTask(); var dispose2 = vm.DisposeAsync().AsTask(); Assert.Same(dispose1, dispose2);
        Assert.False(dispose1.IsCompleted); Assert.True(gate.LastToken.IsCancellationRequested); Assert.False(vm.CancelCommand.CanExecute(null)); Assert.False(vm.OpenCommand.CanExecute(null));
        gate.Release.SetResult(); await Task.WhenAll(read, dispose1, dispose2); Assert.Null(vm.Plot); Assert.Empty(vm.Runs); Assert.False(vm.IsBusy); Assert.Equal(0, gate.DisposeCalls);
    }

    [Fact]
    public async Task ClosingFromABusyBindingNotificationReservesAndDrainsTheReadBeforeDisposalCompletes()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 1);
        var gate = new ReadGateArchive(archive); var vm = new PagedRunHistoryViewModel(new(gate));
        Task? first = null, reentrant = null; var closing = false; var finishedInsideNotification = false;
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(vm.IsBusy) || !vm.IsBusy) return;
            if (!closing) { closing = true; first = vm.DisposeAsync().AsTask(); finishedInsideNotification = first.IsCompleted; }
            else if (reentrant is null) reentrant = vm.DisposeAsync().AsTask();
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.NotNull(first); Assert.Same(first, reentrant); Assert.False(finishedInsideNotification); await first!;
        Assert.Equal(0, gate.BrowseCalls); Assert.Equal(0, gate.PageReadCalls); Assert.False(vm.IsBusy); Assert.Empty(vm.Runs); Assert.False(vm.CanRead); Assert.Equal(0, gate.DisposeCalls);
    }

    [Fact]
    public async Task SupersededMetadataCannotStartAPlotReadForAnOldRun()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 1, "first"); await SaveAsync(archive, 1, "second");
        var gate = new ReadGateArchive(archive) { BlockMetadata = true }; await using var vm = new PagedRunHistoryViewModel(new(gate));
        await vm.RefreshCommand.ExecuteAsync(null); var other = vm.Runs.Single(r => r.Id != vm.SelectedRun!.Id);
        var read = vm.OpenCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedRun = other; gate.Release.SetResult(); await read;
        Assert.Equal(0, gate.PageReadCalls); Assert.Equal(other.Id, vm.SelectedRun!.Id); Assert.Null(vm.Plot); Assert.Empty(vm.Axes);
        gate.BlockMetadata = false; await vm.OpenCommand.ExecuteAsync(null); Assert.Equal(other.Id, vm.Plot!.Metadata.Header.Id);
    }

    [Fact]
    public async Task EditingJumpInputDuringAReadKeepsTheNewInputAndDiscardsOldNavigation()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); await SaveAsync(archive, 505);
        var gate = new ReadGateArchive(archive); await using var vm = new PagedRunHistoryViewModel(new(gate)); await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null); var original = vm.Plot;
        gate.Block = true; var read = vm.NextPlotCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.StartRowText = "200"; gate.Release.SetResult(); await read;
        Assert.Equal("200", vm.StartRowText); Assert.Same(original, vm.Plot); Assert.False(vm.IsBusy);
        gate.Block = false; await vm.JumpCommand.ExecuteAsync(null); Assert.Equal(200, vm.Plot!.Samples[0].RowIndex);
    }

    [Fact]
    public async Task ArchiveReplacementRequiresExplicitReloadAndKeepsTheLastTrustedPage()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var id = await SaveAsync(archive, 505);
        await using var vm = new PagedRunHistoryViewModel(new(archive)); await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null); var trusted = vm.Plot;
        var path = Path.Combine(folder.Path, id.Value.ToString("N"), "metadata.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!; json["name"] = "replacement"; await File.WriteAllTextAsync(path, json.ToJsonString());
        await vm.NextPlotCommand.ExecuteAsync(null); Assert.Same(trusted, vm.Plot); Assert.True(vm.NeedsReload); Assert.Contains("重新载入", vm.Message);
        Assert.False(vm.NextPlotCommand.CanExecute(null)); Assert.False(vm.ReadPlotCommand.CanExecute(null)); Assert.False(vm.JumpCommand.CanExecute(null)); Assert.True(vm.ReloadCommand.CanExecute(null));
        await vm.ReloadCommand.ExecuteAsync(null); Assert.False(vm.NeedsReload); Assert.NotEqual(trusted!.Version, vm.Plot!.Version); Assert.Equal("replacement", vm.Plot.Metadata.Header.Name);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("unit")]
    [InlineData("syntax")]
    public async Task ScientificCorruptionOutsideTheVisiblePageDisablesNavigationAndPreservesTrustedData(string corruption)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var id = await SaveAsync(archive, 505);
        await using var vm = new PagedRunHistoryViewModel(new(archive)); await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null); var original = vm.Plot;
        var path = Path.Combine(folder.Path, id.Value.ToString("N"), "rows.ndjson"); var lines = await File.ReadAllLinesAsync(path);
        var row = JsonNode.Parse(lines[^1])!;
        if (corruption == "sequence") row["index"] = 999;
        if (corruption == "unit") row["values"]!["y"]!["unit"] = "Tesla";
        lines[^1] = corruption == "syntax" ? "{broken" : row.ToJsonString(); await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n");
        if (corruption == "syntax") await vm.OpenCommand.ExecuteAsync(null); else await vm.NextPlotCommand.ExecuteAsync(null);
        Assert.Same(original, vm.Plot); Assert.True(vm.NeedsReload); Assert.Contains("读取失败", vm.Message);
        Assert.False(vm.NextPlotCommand.CanExecute(null)); await vm.ReloadCommand.ExecuteAsync(null); Assert.Same(original, vm.Plot); Assert.True(vm.NeedsReload);
    }

    [Fact]
    public async Task UnfinalizedTailAndEmptyJumpStayVisibleWithoutClaimsAboutPhysicalState()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path); var id = await SaveAsync(archive, 1, finalized: false);
        await File.AppendAllTextAsync(Path.Combine(folder.Path, id.Value.ToString("N"), "rows.ndjson"), "{incomplete");
        await using var vm = new PagedRunHistoryViewModel(new(archive)); await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null);
        Assert.Single(vm.Plot!.Samples); Assert.Contains("不完整", vm.Message); Assert.Contains("未结束记录", vm.RecordStatus); Assert.Contains("未知", vm.RecordStatus);
        vm.StartRowText = "2147483648"; Assert.True(vm.JumpCommand.CanExecute(null)); await vm.JumpCommand.ExecuteAsync(null);
        Assert.Empty(vm.Plot.Samples); Assert.Contains("没有数据", vm.PageSummary); Assert.False(vm.NextPlotCommand.CanExecute(null));
    }

    private static async Task<RunId> SaveAsync(FileRunArchive archive, int rows, string name = "history", bool finalized = true)
    {
        var x = new ParameterDescriptor(new("x"), "Temperature", PhysicalUnit.Kelvin, ParameterAccess.Read);
        var y = new ParameterDescriptor(new("y"), "Resistance", PhysicalUnit.Ohm, ParameterAccess.Read);
        var data = new RunDataSet(RunId.New(), name, DateTimeOffset.UtcNow, new([new(x), new(y, [x.Id], optional: true)]), new Dictionary<string, Quantity>(), []);
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        for (var i = 0; i < rows; i++)
        {
            var values = new Dictionary<ParameterId, Quantity> { [x.Id] = new(300 - i * .1, PhysicalUnit.Kelvin) };
            if (i != 2) values[y.Id] = new(i, PhysicalUnit.Ohm);
            var row = new MeasurementDataRow(data.StartedAt.AddMilliseconds(i), values); data.Append(row); await archive.OnRowAsync(data.Id, row, CancellationToken.None);
        }
        if (finalized) { data.Complete(MeasurementRunState.Completed, data.StartedAt.AddSeconds(rows + 1)); await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None); }
        return data.Id;
    }

    private sealed class ReadGateArchive(IRunArchive inner) : IRunArchive
    {
        public bool Block { get; set; }
        public bool BlockMetadata { get; set; }
        public bool BlockExport { get; set; }
        public RunId? LastExportId { get; private set; }
        public string? LastExportPath { get; private set; }
        public int PageReadCalls, BrowseCalls, FullLoadCalls, FullCatalogCalls, DisposeCalls;
        public CancellationToken LastToken { get; private set; }
        public RunArchiveRowQuery? LastQuery { get; private set; }
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<ArchivedRunPage> ReadPageAsync(RunId id, RunArchiveRowQuery query, CancellationToken token)
        {
            PageReadCalls++; LastToken = token; LastQuery = query;
            if (Block) { Entered.TrySetResult(); await Release.Task; }
            // Intentionally ignore cancellation to exercise stale-result guards.
            return await inner.ReadPageAsync(id, query, CancellationToken.None);
        }
        public Task<RunArchiveCatalogPage> BrowseAsync(RunArchiveCatalogQuery query, CancellationToken token) { BrowseCalls++; return inner.BrowseAsync(query, token); }
        public async Task<RunArchiveMetadata> ReadMetadataAsync(RunId id, CancellationToken token)
        {
            if (BlockMetadata) { LastToken = token; Entered.TrySetResult(); await Release.Task; }
            return await inner.ReadMetadataAsync(id, CancellationToken.None);
        }
        public Task<RunArchiveCatalog> ListAsync(CancellationToken token) { FullCatalogCalls++; throw new InvalidOperationException("Full catalog is not a paged UI operation."); }
        public Task<ArchivedRun> LoadAsync(RunId id, CancellationToken token, int maximumRows = 100000) { FullLoadCalls++; throw new InvalidOperationException("Full load is not a paged UI operation."); }
        public async Task ExportCsvAsync(RunId id, string path, CancellationToken token)
        {
            LastExportId = id; LastExportPath = path; LastToken = token;
            if (BlockExport) { Entered.TrySetResult(); await Release.Task; }
            await inner.ExportCsvAsync(id, path, BlockExport ? CancellationToken.None : token);
        }
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => inner.OnStartedAsync(dataSet, token);
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) => inner.OnRowAsync(id, row, token);
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => inner.OnFinalizedAsync(dataSet, token);
        public ValueTask DisposeAsync() { DisposeCalls++; return inner.DisposeAsync(); }
    }
    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icw-paged-desktop-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}

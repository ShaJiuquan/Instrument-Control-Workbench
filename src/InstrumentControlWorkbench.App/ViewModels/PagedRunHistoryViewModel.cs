using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

// UI-thread-owned page state. The composition root owns the shared history
// service; disposing this screen cancels/drains its reads, not shared storage.
public partial class PagedRunHistoryViewModel(RunHistoryService history, string? exportDirectory = null) : ObservableObject, IAsyncDisposable
{
    private readonly RunHistoryService historyService = history ?? throw new ArgumentNullException(nameof(history));
    private CancellationTokenSource? activeRead;
    private Task pendingRead = Task.CompletedTask;
    private Task? disposal;
    private long revision;
    private bool disposed, assigningAxes, assigningCatalog, needsReload;
    private string searchText = "", startRowText = "1", message = "刷新目录后选择运行。";
    private string? catalogSearch;
    private RunArchiveCatalogPage? catalog;
    private HistoryPlotPage? plot;
    private RunArchiveSummary? selectedRun;
    private ParameterDescriptor? xAxis, yAxis;
    private IReadOnlyList<ParameterDescriptor> axes = Array.Empty<ParameterDescriptor>();

    public bool IsBusy => activeRead is not null;
    public bool CanRead => !disposed && !IsBusy;
    public bool NeedsReload => needsReload;
    public RunArchiveCatalogPage? Catalog => catalog;
    public IReadOnlyList<RunArchiveSummary> Runs => catalog?.Runs ?? Array.Empty<RunArchiveSummary>();
    public HistoryPlotPage? Plot => plot;
    public IReadOnlyList<ParameterDescriptor> Axes => axes;
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public string SearchText
    {
        get => searchText;
        set { ArgumentNullException.ThrowIfNull(value); if (SetProperty(ref searchText, value)) { CancelPending(); NotifyCommands(); } }
    }
    public string StartRowText
    {
        get => startRowText;
        set { ArgumentNullException.ThrowIfNull(value); if (SetProperty(ref startRowText, value)) { CancelPending(); NotifyCommands(); } }
    }
    public RunArchiveSummary? SelectedRun
    {
        get => selectedRun;
        set
        {
            // ItemsSource replacement can synchronously clear a ListBox's
            // selection. Reconcile it after publishing the new catalog page.
            if (value is null && assigningCatalog) return;
            var oldId = selectedRun?.Id;
            if (!SetProperty(ref selectedRun, value)) return;
            if (oldId != value?.Id)
            {
                CancelPending(); axes = Array.Empty<ParameterDescriptor>(); OnPropertyChanged(nameof(Axes));
                AssignAxes(null, null); PublishPlot(null); SetNeedsReload(false);
                Message = value is null ? "选择运行以读取数据。" : "打开所选运行以读取首个数据页。";
                ExportPath = value is null || exportDirectory is null ? "" : System.IO.Path.Combine(exportDirectory, $"{value.Id.Value:N}.csv");
            }
            NotifyCommands();
        }
    }
    public ParameterDescriptor? XAxis
    {
        get => xAxis;
        set { if (SetProperty(ref xAxis, value) && !assigningAxes) InvalidateAxes(); }
    }
    public ParameterDescriptor? YAxis
    {
        get => yAxis;
        set { if (SetProperty(ref yAxis, value) && !assigningAxes) InvalidateAxes(); }
    }
    public string PageSummary => plot is null ? "未读取曲线页" : plot.Samples.Count == 0
        ? $"所选范围没有数据 · 已验证 {plot.ObservedRowCount} 个源记录"
        : $"源记录 {plot.Samples[0].RowIndex}–{plot.Samples[^1].RowIndex} / {plot.ObservedRowCount}";
    public string QualitySummary => plot is null ? "" :
        $"缺测 {plot.Samples.Count(s => !s.HasCoordinates)} · 采集失败 {plot.Samples.Count(s => s.QualityState == ReadingQualityState.ContainsFailedAcquisitions)} · 告警 {plot.Samples.Count(s => s.QualityState == ReadingQualityState.ObservedWithIssues)} · 部分质量 {plot.Samples.Count(s => s.QualityState == ReadingQualityState.PartiallyObserved)} · 未观察质量 {plot.Samples.Count(s => s.QualityState == ReadingQualityState.Unobserved)}";
    public string RecordStatus => plot is null ? "" : !plot.Metadata.IsFinalized
        ? "未结束记录，设备当前状态未知。" : "已结束的历史记录，设备当前状态未知。";

    private bool CanOpen() => CanRead && selectedRun is not null;
    private bool CanPlot() => CanOpen() && xAxis is not null && yAxis is not null &&
        axes.Any(a => a.Id == xAxis.Id) && axes.Any(a => a.Id == yAxis.Id);
    private bool CanReadPlot() => CanPlot() && !needsReload;
    private bool CanNextCatalog() => CanRead && catalog?.HasMore == true && catalogSearch == SearchFilter();
    private bool CanNextPlot() => CanPlot() && !needsReload && plot?.NextOffset is not null;
    private bool CanPreviousPlot() => CanPlot() && !needsReload && plot is { Offset: > 0 };
    private bool CanJump() => CanPlot() && !needsReload && TryGetOffset(out _);
    private bool CanCancel() => IsBusy && !disposed;
    private string? SearchFilter() => string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
    private bool TryGetOffset(out int offset)
    {
        offset = 0;
        if (startRowText.Length > 10 || !long.TryParse(startRowText, NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 1 || first > (long)int.MaxValue + 1) return false;
        offset = (int)(first - 1); return true;
    }

    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task RefreshAsync() => RunReadAsync((token, stamp) => ReadCatalogAsync(null, token, stamp));
    [RelayCommand(CanExecute = nameof(CanNextCatalog))]
    private Task NextCatalogAsync() => CanNextCatalog() ? RunReadAsync((token, stamp) => ReadCatalogAsync(catalog!.NextCursor, token, stamp)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => CanOpen() ? RunReadAsync(OpenSelectedAsync) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanReadPlot))]
    private Task ReadPlotAsync() => CanReadPlot() ? RunReadAsync((token, stamp) => ReadSelectedPlotAsync(0, plot?.Version, token, stamp)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanNextPlot))]
    private Task NextPlotAsync() => CanNextPlot() ? RunReadAsync((token, stamp) => ReadSelectedPlotAsync(plot!.NextOffset!.Value, plot.Version, token, stamp)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanPreviousPlot))]
    private Task PreviousPlotAsync() => CanPreviousPlot() ? RunReadAsync((token, stamp) => ReadSelectedPlotAsync(Math.Max(0, plot!.Offset - 500), plot.Version, token, stamp)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanJump))]
    private Task JumpAsync() => CanJump() && TryGetOffset(out var offset) ? RunReadAsync((token, stamp) => ReadSelectedPlotAsync(offset, plot?.Version, token, stamp)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanPlot))]
    private Task ReloadAsync() => CanPlot() ? RunReadAsync((token, stamp) => ReadSelectedPlotAsync(0, null, token, stamp)) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CancelPending(); Message = "已取消读取，已显示的数据保持上次校验结果。";
    }

    private async Task ReadCatalogAsync(RunArchiveCatalogCursor? after, CancellationToken token, long stamp)
    {
        var filter = SearchFilter(); var page = await historyService.BrowseAsync(new(nameContains: filter, after: after), token);
        if (!IsCurrent(stamp, token)) return;
        var previousId = selectedRun?.Id;
        assigningCatalog = true;
        try
        {
            catalog = page; catalogSearch = filter; OnPropertyChanged(nameof(Catalog)); OnPropertyChanged(nameof(Runs));
            if (!IsCurrent(stamp, token)) return;
            assigningCatalog = false;
            SelectedRun = page.Runs.FirstOrDefault(r => r.Id == previousId) ?? page.Runs.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedRun));
        }
        finally { assigningCatalog = false; }
        Message = $"本页 {page.Runs.Count} 个运行 · 另有 {page.MatchingRuns - page.Runs.Count} 个匹配 · 无法读取 {page.IssueCount} 个目录";
        if (page.HasOmittedIssues) Message += "（部分错误详情未展开）";
    }
    private async Task OpenSelectedAsync(CancellationToken token, long stamp)
    {
        try
        {
            var id = selectedRun!.Id; var metadata = await historyService.ReadMetadataAsync(id, token);
            if (!IsCurrent(stamp, token)) return;
            var columns = metadata.Header.Schema.Columns;
            var y = columns.FirstOrDefault(c => c.DependsOn.Count != 0) ?? columns[Math.Min(1, columns.Count - 1)];
            var x = y.DependsOn.Count != 0 ? columns.First(c => c.Parameter.Id == y.DependsOn[0]) : columns[0];
            var page = await historyService.ReadPlotPageAsync(id, new(), x.Parameter.Id, y.Parameter.Id, token);
            if (!IsCurrent(stamp, token)) return;
            AcceptPlot(page);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (IsCurrent(stamp, token) && plot is not null) SetNeedsReload(true);
            throw;
        }
    }
    private async Task ReadSelectedPlotAsync(int offset, RunArchiveReadVersion? version, CancellationToken token, long stamp)
    {
        try
        {
            var page = await historyService.ReadPlotPageAsync(selectedRun!.Id, new(offset, expectedVersion: version), xAxis!.Id, yAxis!.Id, token);
            if (IsCurrent(stamp, token)) AcceptPlot(page);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (IsCurrent(stamp, token)) SetNeedsReload(true);
            throw;
        }
    }
    private void AcceptPlot(HistoryPlotPage page)
    {
        assigningAxes = true;
        try
        {
            axes = Array.AsReadOnly(page.Metadata.Header.Schema.Columns.Select(c => c.Parameter).ToArray()); OnPropertyChanged(nameof(Axes));
            AssignAxes(page.XAxis, page.YAxis);
        }
        finally { assigningAxes = false; }
        PublishPlot(page); SetNeedsReload(false);
        StartRowText = ((long)page.Offset + 1).ToString(CultureInfo.InvariantCulture);
        Message = page.HasIncompleteTail ? "校验通过，末尾不完整记录已排除。" : "校验通过。";
    }
    private void AssignAxes(ParameterDescriptor? x, ParameterDescriptor? y)
    {
        assigningAxes = true;
        try { XAxis = x; YAxis = y; }
        finally { assigningAxes = false; }
    }
    private void InvalidateAxes()
    {
        CancelPending(); PublishPlot(null); SetNeedsReload(false); StartRowText = "1";
        Message = "坐标轴已更改，读取曲线以验证所选数据。"; NotifyCommands();
    }
    private void PublishPlot(HistoryPlotPage? page)
    {
        plot = page; OnPropertyChanged(nameof(Plot)); OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(QualitySummary)); OnPropertyChanged(nameof(RecordStatus));
        OnPropertyChanged(nameof(LoadedName)); OnPropertyChanged(nameof(Details)); OnPropertyChanged(nameof(Rows));
    }
    private void SetNeedsReload(bool value) { if (SetProperty(ref needsReload, value, nameof(NeedsReload))) NotifyCommands(); }
    private bool IsCurrent(long stamp, CancellationToken token) => !disposed && stamp == revision && !token.IsCancellationRequested;
    private void CancelPending() { revision++; activeRead?.Cancel(); }
    private Task RunReadAsync(Func<CancellationToken, long, Task> action, string failurePrefix = "读取失败")
    {
        if (!CanRead) return Task.CompletedTask;
        var cancellation = new CancellationTokenSource(); activeRead = cancellation; var stamp = revision;
        // Bindings can synchronously close the screen on a busy notification.
        // Reserve the actual operation task before publishing any state.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = FinishAsync(); pendingRead = operation;
        try { NotifyCommands(); }
        finally { start.SetResult(); }
        return operation;
        async Task FinishAsync()
        {
            await start.Task;
            try { cancellation.Token.ThrowIfCancellationRequested(); await action(cancellation.Token, stamp); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { if (IsCurrent(stamp, cancellation.Token)) Message = $"{failurePrefix}：{error.Message}" + (needsReload ? "；请重新载入以验证当前归档。" : ""); }
            finally { activeRead = null; cancellation.Dispose(); NotifyCommands(); }
        }
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanRead));
        RefreshCommand.NotifyCanExecuteChanged(); NextCatalogCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged();
        ReadPlotCommand.NotifyCanExecuteChanged(); NextPlotCommand.NotifyCanExecuteChanged(); JumpCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        PreviousPlotCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
    }
    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        disposed = true; disposal = FinishDisposalAsync(); CancelPending();
        try { NotifyCommands(); }
        finally { start.SetResult(); }
        return new(disposal);
        async Task FinishDisposalAsync()
        {
            await start.Task;
            await pendingRead;
            pendingRead = Task.CompletedTask;
            catalog = null; axes = Array.Empty<ParameterDescriptor>(); AssignAxes(null, null); PublishPlot(null);
            OnPropertyChanged(nameof(Catalog)); OnPropertyChanged(nameof(Runs)); OnPropertyChanged(nameof(Axes));
        }
    }
}

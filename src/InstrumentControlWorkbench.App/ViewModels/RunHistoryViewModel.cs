using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class RunHistoryViewModel(RunHistoryService history, string exportDirectory) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private RunHistoryItem? selectedRun;
    [ObservableProperty] private string message = "刷新后查看已保存的运行。";
    [ObservableProperty] private string exportPath = "";
    [ObservableProperty] private string details = "选择运行并打开数据。";
    [ObservableProperty] private string loadedName = "历史数据";
    public ObservableCollection<RunHistoryItem> Runs { get; } = new();
    public ObservableCollection<PlanRowItem> Rows { get; } = new();
    public bool CanRefresh => !disposed && !IsBusy;
    private bool CanOpen() => CanRefresh && SelectedRun is not null;
    private bool CanExport() => CanOpen() && !string.IsNullOrWhiteSpace(ExportPath);
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnExportPathChanged(string value) => NotifyCommands();
    partial void OnSelectedRunChanged(RunHistoryItem? value)
    {
        if (value is not null) ExportPath = Path.Combine(exportDirectory, $"{value.Metadata.Header.Id.Value:N}.csv");
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanRefresh));
        RefreshCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var previous = SelectedRun?.Metadata.Header.Id;
            var catalog = await history.RefreshAsync(lifetime.Token);
            Runs.Clear(); foreach (var run in catalog.Runs) Runs.Add(new(run));
            SelectedRun = Runs.FirstOrDefault(r => r.Metadata.Header.Id == previous) ?? Runs.FirstOrDefault();
            Message = catalog.Issues.Count == 0 ? $"{Runs.Count} 个已保存运行。" :
                $"{Runs.Count} 个运行；{catalog.Issues.Count} 个目录无法读取：{catalog.Issues[0].Message}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Message = $"历史读取失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (SelectedRun is not { } selected) return;
        IsBusy = true;
        try
        {
            var saved = await history.OpenAsync(selected.Metadata.Header.Id, lifetime.Token);
            Rows.Clear();
            var start = Math.Max(0, saved.DataSet.Rows.Count - 200);
            for (var i = start; i < saved.DataSet.Rows.Count; i++)
            {
                var row = saved.DataSet.Rows[i];
                Rows.Add(new((i + 1).ToString(CultureInfo.InvariantCulture), row.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
                    string.Join(" · ", row.Values.Select(v => $"{v.Key}: {v.Value.Value.ToString("G6", CultureInfo.InvariantCulture)} {v.Value.Unit}"))));
            }
            LoadedName = saved.DataSet.Name;
            var sample = saved.DataSet.Metadata?.SampleName ?? "未记录样品名称";
            Details = $"样品：{sample} · {saved.DataSet.Instruments.Count} 台设备 · {saved.DataSet.Rows.Count} 行 · 显示最近 200 行";
            var loadedItem = selected with { Metadata = new(saved.DataSet, saved.DataSet.Rows.Count, null) };
            Message = saved.IsFinalized ? $"归档校验通过 · {loadedItem.Status}" :
                saved.HasIncompleteTail ? "未结束记录；已保留完整数据行，末尾不完整记录被排除。设备当前状态未知。" :
                "运行没有结束记录。可检查部分数据；设备当前状态未知。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Message = $"数据打开失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (SelectedRun is not { } selected) return;
        IsBusy = true;
        try { await history.ExportAsync(selected.Metadata.Header.Id, ExportPath, lifetime.Token); Message = $"已导出：{ExportPath}"; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Message = $"导出失败，原文件保留：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; NotifyCommands(); lifetime.Cancel();
        if (RefreshCommand.ExecutionTask is { } refresh) await refresh;
        if (OpenCommand.ExecutionTask is { } open) await open;
        if (ExportCommand.ExecutionTask is { } export) await export;
        await history.DisposeAsync(); lifetime.Dispose();
    }
}
public sealed record RunHistoryItem(RunArchiveMetadata Metadata)
{
    public string Name => Metadata.Header.Name;
    public string Time => Metadata.Header.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Status => Metadata.IsFinalized ? Metadata.Header.State switch
    {
        Core.Measurements.MeasurementRunState.Completed => "完成",
        Core.Measurements.MeasurementRunState.Aborted => "已停止",
        _ => "运行异常",
    } : "未结束记录";
    public string Summary => $"{Status} · {Metadata.RowCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} 行";
}

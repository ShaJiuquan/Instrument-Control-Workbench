using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PagedRunHistoryViewModel
{
    [ObservableProperty] private string exportPath = "";
    [ObservableProperty] private bool connectRecords;
    public string LoadedName => plot?.Metadata.Header.Name ?? "历史曲线";
    public string Details => plot is null ? "打开运行以查看本页数据。" :
        $"样品：{plot.Metadata.Header.Metadata?.SampleName ?? "未记录"} · {plot.Metadata.Header.Instruments.Count} 台设备 · {PageSummary}";
    public IReadOnlyList<HistoryPlotSample> Rows => plot?.Samples ?? Array.Empty<HistoryPlotSample>();
    private bool CanExport() => CanRead && selectedRun is not null && !string.IsNullOrWhiteSpace(ExportPath);
    partial void OnExportPathChanged(string value) => NotifyCommands();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync()
    {
        if (!CanExport()) return Task.CompletedTask;
        // Capture both before busy notifications can synchronously change selection.
        var id = selectedRun!.Id; var path = ExportPath;
        return RunReadAsync(async (token, stamp) =>
        {
            await historyService.ExportAsync(id, path, token);
            if (IsCurrent(stamp, token)) Message = $"完整运行已重新校验并导出：{path}";
        }, "导出失败");
    }
}

public static class HistoryPresentation
{
    public static string Status(bool finalized, MeasurementRunState state) => !finalized ? "未结束记录" : state switch
    {
        MeasurementRunState.Completed => "完成",
        MeasurementRunState.Aborted => "已停止",
        _ => "运行异常",
    };
    public static string Kind(HistoryPlotRowKind kind) => kind switch
    {
        HistoryPlotRowKind.RoutedReading => "路由读数",
        HistoryPlotRowKind.SampleAnalysis => "分析",
        HistoryPlotRowKind.MeasurementGroup => "联合汇总",
        _ => "读数",
    };
    public static string Unit(PhysicalUnit unit) => unit switch
    {
        PhysicalUnit.Dimensionless => "1",
        PhysicalUnit.Volt => "V",
        PhysicalUnit.Ampere => "A",
        PhysicalUnit.Ohm => "Ω",
        PhysicalUnit.Kelvin => "K",
        PhysicalUnit.Tesla => "T",
        PhysicalUnit.Degree => "°",
        PhysicalUnit.Second => "s",
        PhysicalUnit.Hertz => "Hz",
        PhysicalUnit.Coulomb => "C",
        PhysicalUnit.Pascal => "Pa",
        PhysicalUnit.KelvinPerMinute => "K/min",
        PhysicalUnit.TeslaPerSecond => "T/s",
        PhysicalUnit.DegreePerSecond => "°/s",
        PhysicalUnit.Meter => "m",
        PhysicalUnit.OhmPerSquare => "Ω/□",
        PhysicalUnit.OhmMeter => "Ω·m",
        _ => unit.ToString(),
    };
    public static string Value(Quantity? quantity) => quantity is { } q ? $"{q.Value.ToString("G6", CultureInfo.InvariantCulture)} {Unit(q.Unit)}" : "缺测";
    public static string Quality(HistoryPlotSample sample)
    {
        var status = sample.QualityState switch
        {
            ReadingQualityState.ContainsFailedAcquisitions => "采集失败",
            ReadingQualityState.ObservedWithIssues => "采集告警",
            ReadingQualityState.ObservedNormal => "已观察质量正常",
            ReadingQualityState.PartiallyObserved => "质量部分观察",
            _ => "质量未观察",
        };
        return sample.QualityIssues == ReadingQualityIssues.None ? status : $"{status} · {sample.QualityIssues}";
    }
}

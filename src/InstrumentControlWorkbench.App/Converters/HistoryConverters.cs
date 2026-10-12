using System;
using System.Globalization;
using Avalonia.Data.Converters;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.Converters;

public sealed class HistorySummaryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is RunArchiveSummary run
        ? $"{HistoryPresentation.Status(run.IsFinalized, run.State)} · {run.RowCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} 行 · {run.InstrumentCount} 台设备" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class HistoryValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => HistoryPresentation.Value(value is Quantity q ? q : null);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class HistorySampleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is HistoryPlotSample sample
        ? $"{HistoryPresentation.Kind(sample.Kind)} · {HistoryPresentation.Quality(sample)}" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class HistoryLocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is DateTimeOffset time
        ? time.ToLocalTime().ToString(parameter is "clock" ? "HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

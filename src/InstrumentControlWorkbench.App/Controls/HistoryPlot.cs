using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.App.Controls;

// Presentation only: no IO, resampling, sorting, unit conversion or fitted data.
public sealed class HistoryPlot : Control
{
    public static readonly StyledProperty<HistoryPlotPage?> PageProperty = AvaloniaProperty.Register<HistoryPlot, HistoryPlotPage?>(nameof(Page));
    public static readonly StyledProperty<bool> ConnectRecordsProperty = AvaloniaProperty.Register<HistoryPlot, bool>(nameof(ConnectRecords));
    private HistoryPlotGeometry? geometry;
    private Point? cursor;
    public HistoryPlotPage? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public bool ConnectRecords { get => GetValue(ConnectRecordsProperty); set => SetValue(ConnectRecordsProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) { geometry = HistoryPlotGeometry.Create(Page); cursor = null; InvalidateVisual(); }
        if (change.Property == ConnectRecordsProperty) InvalidateVisual();
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); InvalidateVisual(); }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); cursor = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); cursor = null; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        var area = new Rect(72, 29, Math.Max(1, Bounds.Width - 96), Math.Max(1, Bounds.Height - 82));
        if (area.Width < 40 || area.Height < 40) return;
        var grid = new Pen(Brush("Line", "#E7EAF1"), 1); var accent = Brush("Accent", "#7458E8");
        var g = geometry; var page = Page;
        if (page is not null) Text(context, $"{page.YAxis.Label} / {HistoryPresentation.Unit(page.YAxis.Unit)}", 8, 3);
        for (var i = 0; i <= 4; i++)
        {
            var f = i / 4d; var x = area.Left + f * area.Width; var y = area.Bottom - f * area.Height;
            context.DrawLine(grid, new(x, area.Top), new(x, area.Bottom)); context.DrawLine(grid, new(area.Left, y), new(area.Right, y));
            if (g is not null)
            {
                if (g.XRange.Minimum != g.XRange.Maximum || i == 2) Text(context, Label(g.XRange.At(f)), Math.Max(0, Math.Min(x - 18, Bounds.Width - 76)), area.Bottom + 10);
                if (g.YRange.Minimum != g.YRange.Maximum || i == 2) Text(context, Label(g.YRange.At(f)), 4, y - 7);
            }
        }
        if (page is not null) Text(context, $"{page.XAxis.Label} / {HistoryPresentation.Unit(page.XAxis.Unit)}", area.Left + 10, area.Bottom + 32);
        if (g is null)
        {
            Text(context, page is null ? "打开历史运行以查看曲线" : "所选页没有完整的 X / Y 坐标", area.Left + 12, area.Top + area.Height / 2);
            return;
        }
        Point Map(HistoryPlotVertex vertex) => new(area.Left + vertex.X * area.Width, area.Bottom - vertex.Y * area.Height);
        using (context.PushClip(area.Inflate(5)))
        {
            HistoryPlotVertex? previous = null;
            foreach (var vertex in g.Vertices)
            {
                var point = Map(vertex);
                if (ConnectRecords && vertex.ConnectPrevious && previous is not null) context.DrawLine(new Pen(accent, 1.4), Map(previous), point);
                var color = vertex.Sample.QualityState switch
                {
                    ReadingQualityState.ContainsFailedAcquisitions => Avalonia.Media.Brushes.IndianRed,
                    ReadingQualityState.ObservedWithIssues => Avalonia.Media.Brushes.DarkOrange,
                    ReadingQualityState.Unobserved or ReadingQualityState.PartiallyObserved => Brush("Secondary", "#7D8698"),
                    _ => accent,
                };
                var pen = new Pen(color, 1.6);
                if (vertex.Sample.QualityState == ReadingQualityState.ContainsFailedAcquisitions)
                { context.DrawLine(pen, point - new Vector(4, 4), point + new Vector(4, 4)); context.DrawLine(pen, point + new Vector(-4, 4), point + new Vector(4, -4)); }
                else if (vertex.Sample.Kind == HistoryPlotRowKind.MeasurementGroup) context.DrawRectangle(null, pen, new Rect(point - new Vector(3, 3), new Size(6, 6)));
                else if (vertex.Sample.Kind is HistoryPlotRowKind.RoutedReading or HistoryPlotRowKind.SampleAnalysis)
                {
                    var top = point + new Vector(0, -4); var right = point + new Vector(4, 0); var bottom = point + new Vector(0, 4); var left = point + new Vector(-4, 0);
                    if (vertex.Sample.Kind == HistoryPlotRowKind.SampleAnalysis) { context.DrawLine(pen, top, right + new Vector(0, 3)); context.DrawLine(pen, right + new Vector(0, 3), left + new Vector(0, 3)); context.DrawLine(pen, left + new Vector(0, 3), top); }
                    else { context.DrawLine(pen, top, right); context.DrawLine(pen, right, bottom); context.DrawLine(pen, bottom, left); context.DrawLine(pen, left, top); }
                }
                else context.DrawEllipse(null, pen, point, 2.8, 2.8);
                previous = vertex;
            }
        }
        if (cursor is { } pointer && area.Contains(pointer))
        {
            var nearest = g.Vertices.MinBy(v => { var point = Map(v); return (point.X - pointer.X) * (point.X - pointer.X) + (point.Y - pointer.Y) * (point.Y - pointer.Y); })!;
            var box = new Rect(area.Left + 5, area.Top + 7, Math.Min(390, area.Width - 10), 63);
            context.DrawRectangle(Brush("AccentSoft", "#F3F0FF"), null, box, 4, 4);
            Text(context, $"记录 {nearest.Sample.RowIndex} · {nearest.Sample.Timestamp.ToLocalTime():HH:mm:ss.fff} · {HistoryPresentation.Kind(nearest.Sample.Kind)}", box.Left + 8, box.Top + 8);
            Text(context, $"{HistoryPresentation.Value(nearest.Sample.X)} / {HistoryPresentation.Value(nearest.Sample.Y)}", box.Left + 8, box.Top + 27);
            Text(context, HistoryPresentation.Quality(nearest.Sample), box.Left + 8, box.Top + 44);
        }
    }
    private IBrush Brush(string key, string fallback) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Avalonia.Media.Brush.Parse(fallback);
    private static string Label(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
    private void Text(DrawingContext context, string text, double x, double y) => context.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 10, Brush("Secondary", "#7D8698")), new(x, y));
}

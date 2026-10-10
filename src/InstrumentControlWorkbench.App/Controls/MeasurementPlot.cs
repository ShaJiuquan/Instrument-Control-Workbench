using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using InstrumentControlWorkbench.Core.Measurements;

namespace InstrumentControlWorkbench.App.Controls;

// A small data-backed I–V renderer using the existing Avalonia drawing API.
// It deliberately has no instrument commands or synthetic plot data.
public sealed class MeasurementPlot : Control
{
    public static readonly StyledProperty<ObservableCollection<MeasurementPoint>?> PointsProperty =
        AvaloniaProperty.Register<MeasurementPlot, ObservableCollection<MeasurementPoint>?>(nameof(Points));
    private ObservableCollection<MeasurementPoint>? observed;
    private Point? cursor;
    private bool attached;
    private IBrush Ink => ThemeBrush("Secondary", "#7D8698");
    private IBrush Accent => ThemeBrush("Accent", "#7458E8");
    private Pen GridPen => new(ThemeBrush("Line", "#E7EAF1"), 1);
    private static readonly Pen AxisPen = new(Brush.Parse("#CED3DF"), 1);
    private Pen TracePen => new(Accent, 2);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    public ObservableCollection<MeasurementPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty) { Observe(); InvalidateVisual(); }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Observe();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Observe();
        base.OnDetachedFromVisualTree(e);
    }
    private void Observe()
    {
        if (observed is not null) observed.CollectionChanged -= OnPointsChanged;
        observed = attached ? Points : null;
        if (observed is not null) observed.CollectionChanged += OnPointsChanged;
    }
    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); cursor = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); cursor = null; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var plot = new Rect(62, 29, Math.Max(1, Bounds.Width - 84), Math.Max(1, Bounds.Height - 77));
        if (plot.Width < 20 || plot.Height < 20) return;
        var points = Points;
        var hasData = points is { Count: > 0 };
        var xMin = hasData ? points!.Min(p => p.SourceValue) : -0.2;
        var xMax = hasData ? points!.Max(p => p.SourceValue) : 0.8;
        var yMin = hasData ? points!.Min(p => p.MeasuredValue) * 1e3 : -0.5;
        var yMax = hasData ? points!.Max(p => p.MeasuredValue) * 1e3 : 2;
        if (Math.Abs(xMax - xMin) < 1e-12) { xMin -= 0.5; xMax += 0.5; }
        var yPadding = Math.Max((yMax - yMin) * 0.12, 0.0001);
        yMin -= yPadding;
        yMax += yPadding;
        var rawStep = (yMax - yMin) / (plot.Height < 90 ? 2 : plot.Height < 140 ? 3 : 5);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var fraction = rawStep / magnitude;
        var yStep = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * magnitude;
        yMin = Math.Floor(yMin / yStep) * yStep;
        yMax = Math.Ceiling(yMax / yStep) * yStep;
        DrawText(context, "电流 / mA", 9, 3, 11);
        for (var i = 0; i <= 5; i++)
        {
            var x = plot.Left + plot.Width * i / 5;
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var xValue = xMin + (xMax - xMin) * i / 5;
            var xt = Label(xValue);
            DrawText(context, xt, x - xt.Length * 3.1, plot.Bottom + 10, 11);
        }
        for (var value = yMin; value <= yMax + yStep * 0.01; value += yStep)
        {
            var y = plot.Bottom - (value - yMin) / (yMax - yMin) * plot.Height;
            var yt = Label(value);
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, yt, plot.Left - 13 - yt.Length * 6.1, y - 6, 11);
        }
        context.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);
        context.DrawLine(AxisPen, plot.TopLeft, plot.BottomLeft);
        DrawText(context, "源电压 / V", plot.Left + plot.Width / 2 - 31, plot.Bottom + 30, 11);
        Point Map(MeasurementPoint p) => new(
            plot.Left + (p.SourceValue - xMin) / (xMax - xMin) * plot.Width,
            plot.Bottom - (p.MeasuredValue * 1e3 - yMin) / (yMax - yMin) * plot.Height);
        if (hasData)
        {
            for (var i = 0; i < points!.Count; i++)
            {
                var current = Map(points[i]);
                if (i > 0) context.DrawLine(TracePen, Map(points[i - 1]), current);
                if (points.Count <= 200) context.DrawEllipse(Brushes.White, new Pen(Accent, 1.5), current, 2.7, 2.7);
            }
            if (cursor is { } pointer && plot.Contains(pointer))
            {
                var nearest = points.MinBy(p => Math.Abs(Map(p).X - pointer.X))!;
                var location = Map(nearest);
                context.DrawLine(AxisPen, new Point(location.X, plot.Top), new Point(location.X, plot.Bottom));
                context.DrawEllipse(Accent, null, location, 4, 4);
                var text = $"{nearest.SourceValue:0.000} V  /  {nearest.MeasuredValue * 1e3:0.0000} mA";
                var box = new Rect(Math.Min(location.X + 12, plot.Right - 222), plot.Top + 12, 222, 32);
                context.DrawRectangle(Brush.Parse("#F3F0FF"), null, box, 4, 4);
                DrawText(context, text, box.Left + 11, box.Top + 9, 11, Accent);
            }
        }
        else
        {
            var width = Math.Min(270, plot.Width - 20);
            var box = new Rect(plot.Left + (plot.Width - width) / 2, plot.Top + plot.Height / 2 - 42, width, 83);
            context.DrawRectangle(Brushes.White, null, box, 6, 6);
            DrawText(context, "等待第一次采集", box.Left + (width - 112) / 2, box.Top + 17, 15, Brush.Parse("#4D456B"));
            DrawText(context, "配置右侧参数，开始测量", box.Left + (width - 140) / 2, box.Top + 46, 12);
        }
    }
    private static string Label(double value) => Math.Abs(value) is > 0 and < 0.001
        ? value.ToString("0.#E+0", CultureInfo.InvariantCulture) : value.ToString("0.###", CultureInfo.InvariantCulture);
    private IBrush ThemeBrush(string key, string fallback) =>
        this.TryFindResource(key, out var resource) && resource is IBrush brush ? brush : Brush.Parse(fallback);
    private void DrawText(DrawingContext context, string text, double x, double y, double size, IBrush? brush = null) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter")), size, brush ?? Ink), new Point(x, y));
}

using System;
using System.Collections.Generic;
using System.Linq;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.App.Controls;

public readonly record struct HistoryAxisRange(double Minimum, double Maximum)
{
    public double Normalize(double value)
    {
        if (Minimum == Maximum) return .5;
        var scale = Math.Max(Math.Abs(Minimum), Math.Abs(Maximum));
        return Math.Clamp((value / scale - Minimum / scale) / (Maximum / scale - Minimum / scale), 0, 1);
    }
    public double At(double fraction)
    {
        var scale = Math.Max(Math.Abs(Minimum), Math.Abs(Maximum));
        return scale == 0 ? 0 : Math.Clamp(Minimum / scale * (1 - fraction) + Maximum / scale * fraction, -1, 1) * scale;
    }
}
public sealed record HistoryPlotVertex(HistoryPlotSample Sample, double X, double Y, bool ConnectPrevious);
public sealed record HistoryPlotGeometry(HistoryAxisRange XRange, HistoryAxisRange YRange, IReadOnlyList<HistoryPlotVertex> Vertices)
{
    public static HistoryPlotGeometry? Create(HistoryPlotPage? page)
    {
        if (page is null) return null;
        var coordinates = page.Samples.Where(s => s.HasCoordinates).ToArray();
        if (coordinates.Length == 0) return null;
        var xr = new HistoryAxisRange(coordinates.Min(s => s.X!.Value.Value), coordinates.Max(s => s.X!.Value.Value));
        var yr = new HistoryAxisRange(coordinates.Min(s => s.Y!.Value.Value), coordinates.Max(s => s.Y!.Value.Value));
        var vertices = new List<HistoryPlotVertex>(coordinates.Length); HistoryPlotSample? previous = null;
        foreach (var sample in page.Samples)
        {
            if (!sample.HasCoordinates) { previous = null; continue; }
            var connect = previous is not null && sample.RowIndex == previous.RowIndex + 1 && sample.Kind == previous.Kind &&
                sample.QualityState != ReadingQualityState.ContainsFailedAcquisitions && previous.QualityState != ReadingQualityState.ContainsFailedAcquisitions;
            vertices.Add(new(sample, xr.Normalize(sample.X!.Value.Value), yr.Normalize(sample.Y!.Value.Value), connect));
            previous = sample;
        }
        return new(xr, yr, vertices.AsReadOnly());
    }
}

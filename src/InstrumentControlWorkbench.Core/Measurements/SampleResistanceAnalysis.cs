using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Measurements;

public enum SampleDerivedQuantity { LongitudinalResistance, TransverseResistance, HorizontalMeanResistance, VerticalMeanResistance, SheetResistance, Resistivity }
[Flags]
public enum SampleAnalysisIssues { None = 0, MissingGeometry = 1, NonPositiveVanDerPauwMean = 2, UnrepresentableResult = 4, FailedAcquisition = 8 }
public sealed record SampleAnalysisResult(IReadOnlyDictionary<SampleDerivedQuantity, Quantity> Values, SampleAnalysisIssues Issues);

// Pure measured-input analysis. No device access, field-reversal assumption,
// Hall coefficient, carrier-density inference or fabricated compliance quality.
public static class SampleResistanceAnalysis
{
    public const int AlgorithmVersion = 1;
    public static IReadOnlyList<SampleRouteSelection> Selections(SampleTopology topology) => topology switch
    {
        SampleTopology.HallBar => Array.AsReadOnly(new[] { SampleRouteSelection.HallLongitudinal, SampleRouteSelection.HallTransverse }),
        SampleTopology.VanDerPauw => Array.AsReadOnly(new[] { SampleRouteSelection.VdpAbCd, SampleRouteSelection.VdpBaCd, SampleRouteSelection.VdpAbDc, SampleRouteSelection.VdpBaDc,
            SampleRouteSelection.VdpBcDa, SampleRouteSelection.VdpCbDa, SampleRouteSelection.VdpBcAd, SampleRouteSelection.VdpCbAd }),
        _ => throw new NotSupportedException("Derived analysis currently supports Hall-bar and van der Pauw groups."),
    };
    public static IReadOnlyList<SampleDerivedQuantity> Kinds(SampleTopology topology) => topology switch
    {
        SampleTopology.HallBar => Array.AsReadOnly(new[] { SampleDerivedQuantity.LongitudinalResistance, SampleDerivedQuantity.TransverseResistance, SampleDerivedQuantity.SheetResistance, SampleDerivedQuantity.Resistivity }),
        SampleTopology.VanDerPauw => Array.AsReadOnly(new[] { SampleDerivedQuantity.HorizontalMeanResistance, SampleDerivedQuantity.VerticalMeanResistance, SampleDerivedQuantity.SheetResistance, SampleDerivedQuantity.Resistivity }),
        _ => throw new NotSupportedException("Derived analysis currently supports Hall-bar and van der Pauw groups."),
    };
    public static PhysicalUnit Unit(SampleDerivedQuantity kind) => kind switch
    {
        SampleDerivedQuantity.LongitudinalResistance or SampleDerivedQuantity.TransverseResistance or SampleDerivedQuantity.HorizontalMeanResistance or SampleDerivedQuantity.VerticalMeanResistance => PhysicalUnit.Ohm,
        SampleDerivedQuantity.SheetResistance => PhysicalUnit.OhmPerSquare,
        SampleDerivedQuantity.Resistivity => PhysicalUnit.OhmMeter,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    public static SampleAnalysisResult Calculate(SampleTopology topology, SampleGeometry geometry, IReadOnlyList<Quantity> resistances)
    { ArgumentNullException.ThrowIfNull(resistances); return CalculateAvailable(topology, geometry, resistances.Select(r => (Quantity?)r).ToArray()); }

    public static SampleAnalysisResult CalculateAvailable(SampleTopology topology, SampleGeometry geometry, IReadOnlyList<Quantity?> resistances)
    {
        ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(resistances);
        if (resistances.Count != Selections(topology).Count || resistances.Any(r => r is { Unit: not PhysicalUnit.Ohm }))
            throw new ArgumentException("Analysis needs the complete ordered group of resistances in Ohm.", nameof(resistances));
        var values = new Dictionary<SampleDerivedQuantity, Quantity>(); var issues = resistances.Any(r => r is null) ? SampleAnalysisIssues.FailedAcquisition : SampleAnalysisIssues.None;
        double? sheet = null;
        if (topology == SampleTopology.HallBar)
        {
            if (resistances[0] is { } xx) values.Add(SampleDerivedQuantity.LongitudinalResistance, xx);
            if (resistances[1] is { } xy) values.Add(SampleDerivedQuantity.TransverseResistance, xy);
            if (resistances[0] is { } longitudinal && geometry.Width is { } width && geometry.Length is { } length)
            {
                var r = longitudinal.Value;
                sheet = r == 0 ? 0 : Math.CopySign(Math.Exp(Math.Log(Math.Abs(r)) + Math.Log(width.Value) - Math.Log(length.Value)), r);
            }
            else if (geometry.Width is null || geometry.Length is null) issues |= SampleAnalysisIssues.MissingGeometry;
        }
        else
        {
            double? h = resistances.Take(4).All(r => r is not null) ? MeanAbsolute(resistances.Take(4).Select(r => r!.Value)) : null;
            double? v = resistances.Skip(4).All(r => r is not null) ? MeanAbsolute(resistances.Skip(4).Select(r => r!.Value)) : null;
            if (h is { } horizontal) values.Add(SampleDerivedQuantity.HorizontalMeanResistance, new(horizontal, PhysicalUnit.Ohm));
            if (v is { } vertical) values.Add(SampleDerivedQuantity.VerticalMeanResistance, new(vertical, PhysicalUnit.Ohm));
            if (h > 0 && v > 0) sheet = SolveVanDerPauw(h.Value, v.Value);
            else if (h == 0 || v == 0) issues |= SampleAnalysisIssues.NonPositiveVanDerPauwMean;
        }
        if (sheet is { } rs)
        {
            if (double.IsFinite(rs) && (rs != 0 || topology == SampleTopology.HallBar && resistances[0]?.Value == 0))
            {
                values.Add(SampleDerivedQuantity.SheetResistance, new(rs, PhysicalUnit.OhmPerSquare));
                if (geometry.Thickness is { } thickness)
                {
                    var rho = rs * thickness.Value;
                    if (double.IsFinite(rho) && (rho != 0 || rs == 0)) values.Add(SampleDerivedQuantity.Resistivity, new(rho, PhysicalUnit.OhmMeter));
                    else issues |= SampleAnalysisIssues.UnrepresentableResult;
                }
                else issues |= SampleAnalysisIssues.MissingGeometry;
            }
            else issues |= SampleAnalysisIssues.UnrepresentableResult;
        }
        return new(new ReadOnlyDictionary<SampleDerivedQuantity, Quantity>(values), issues);
    }
    private static double MeanAbsolute(IEnumerable<Quantity> values)
    {
        var items = values.Select(v => Math.Abs(v.Value)).ToArray(); var scale = items.Max();
        return scale == 0 ? 0 : scale * (items.Sum(v => v / scale) / items.Length);
    }
    private static double SolveVanDerPauw(double h, double v)
    {
        // Solve for z = pi * max(Rh,Rv) / Rs in log space. Keeping log(Rmin/Rmax)
        // avoids overflowing a resistance bracket or underflowing extreme ratios.
        var large = Math.Max(h, v); var logRatio = Math.Log(Math.Min(h, v)) - Math.Log(large);
        var lo = 0.0; var hi = Math.Max(1, -logRatio + 1);
        for (var i = 0; i < 200; i++)
        {
            var z = lo + (hi - lo) / 2; var logSmall = logRatio + Math.Log(z);
            var small = Math.Exp(logSmall);
            var logComplement = logSmall < -7
                ? logSmall + Math.Log(1 - small / 2 + small * small / 6 - small * small * small / 24 + Math.Pow(small, 4) / 120)
                : Math.Log(1 - Math.Exp(-small));
            if (logComplement + z < 0) lo = z; else hi = z;
            if (hi - lo <= z * 1e-13) break;
        }
        return Math.PI * (large / (lo + (hi - lo) / 2));
    }
}

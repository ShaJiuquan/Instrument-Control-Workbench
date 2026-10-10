using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class SampleAnalysisMathTests
{
    [Theory]
    [InlineData(100, 50)]
    [InlineData(-100, -50)]
    [InlineData(0, 0)]
    public void HallGeometryPreservesMeasuredSignsAndUnits(double longitudinal, double expectedSheet)
    {
        var result = SampleResistanceAnalysis.Calculate(SampleTopology.HallBar, new(new(0.002, PhysicalUnit.Meter), new(0.004, PhysicalUnit.Meter), new(1e-7, PhysicalUnit.Meter)),
            [new(longitudinal, PhysicalUnit.Ohm), new(-3, PhysicalUnit.Ohm)]);
        Assert.Equal(new(-3, PhysicalUnit.Ohm), result.Values[SampleDerivedQuantity.TransverseResistance]);
        Assert.Equal(PhysicalUnit.OhmPerSquare, result.Values[SampleDerivedQuantity.SheetResistance].Unit);
        Assert.Equal(expectedSheet, result.Values[SampleDerivedQuantity.SheetResistance].Value, 10);
        Assert.Equal(PhysicalUnit.OhmMeter, result.Values[SampleDerivedQuantity.Resistivity].Unit);
        Assert.Equal(expectedSheet * 1e-7, result.Values[SampleDerivedQuantity.Resistivity].Value, 14);
        Assert.Equal(SampleAnalysisIssues.None, result.Issues);
    }
    [Fact]
    public void MissingGeometryRetainsRawHallQuantitiesWithoutInventingZeroSheetValues()
    {
        var result = SampleResistanceAnalysis.Calculate(SampleTopology.HallBar, SampleGeometry.Empty, [new(100, PhysicalUnit.Ohm), new(3, PhysicalUnit.Ohm)]);
        Assert.Equal(2, result.Values.Count); Assert.Equal(SampleAnalysisIssues.MissingGeometry, result.Issues);
    }
    [Theory]
    [InlineData(0, PhysicalUnit.Meter)]
    [InlineData(-1, PhysicalUnit.Meter)]
    [InlineData(1, PhysicalUnit.Volt)]
    public void GeometryRejectsNonpositiveOrWrongUnitDimensions(double value, PhysicalUnit unit)
        => Assert.Throws<ArgumentException>(() => new SampleGeometry(thickness: new(value, unit)));

    [Fact]
    public void IsotropicSignedVdpMatchesAnalyticSheetResistance()
    {
        var result = Vdp(25, 25); var sheet = result.Values[SampleDerivedQuantity.SheetResistance].Value;
        Assert.True(Math.Abs(sheet / (Math.PI * 25 / Math.Log(2)) - 1) < 1e-12);
        Assert.Equal(SampleAnalysisIssues.None, result.Issues);
    }
    [Theory]
    [InlineData(12.5, 47)]
    [InlineData(1e-12, 1e12)]
    [InlineData(1e-100, 1e100)]
    [InlineData(1e-200, 1e-200)]
    [InlineData(1e200, 1e200)]
    public void VdpIsSymmetricAndSatisfiesTheEquationOverWideScales(double h, double v)
    {
        var a = Vdp(h, v).Values[SampleDerivedQuantity.SheetResistance].Value;
        var b = Vdp(v, h).Values[SampleDerivedQuantity.SheetResistance].Value;
        Assert.True(Math.Abs(a / b - 1) < 1e-12);
        var residual = Math.Exp(-Math.PI * (h / a)) + Math.Exp(-Math.PI * (v / a)) - 1;
        Assert.True(Math.Abs(residual) < 1e-12, $"Residual {residual}");
    }
    [Fact]
    public void ExtremeRatioDoesNotUnderflowTheSolverInputRatio()
    {
        var result = Vdp(double.Epsilon, 1e300);
        var sheet = result.Values[SampleDerivedQuantity.SheetResistance].Value;
        Assert.True(sheet > 1e297);
        var z = Math.PI * (1e300 / sheet);
        Assert.True(Math.Abs(z + Math.Log(z) + Math.Log(double.Epsilon) - Math.Log(1e300)) < 1e-9);
        Assert.False(result.Issues.HasFlag(SampleAnalysisIssues.UnrepresentableResult));
    }
    [Fact]
    public void ZeroVdpGroupIsReportedWithMeansAndNoFakeSheetResistance()
    {
        var result = Vdp(0, 25); Assert.Equal(2, result.Values.Count);
        Assert.Equal(SampleAnalysisIssues.NonPositiveVanDerPauwMean, result.Issues);
    }
    [Fact]
    public void UnrepresentableSheetResistanceRetainsFiniteRawMeans()
    {
        var result = Vdp(double.MaxValue, double.MaxValue);
        Assert.Equal(double.MaxValue, result.Values[SampleDerivedQuantity.HorizontalMeanResistance].Value);
        Assert.Equal(SampleAnalysisIssues.UnrepresentableResult, result.Issues);
        Assert.False(result.Values.ContainsKey(SampleDerivedQuantity.SheetResistance));
    }
    [Fact]
    public void WrongUnitsAndIncompleteSetsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SampleResistanceAnalysis.Calculate(SampleTopology.HallBar, SampleGeometry.Empty, [new(1, PhysicalUnit.Volt), new(1, PhysicalUnit.Ohm)]));
        Assert.Throws<ArgumentException>(() => SampleResistanceAnalysis.Calculate(SampleTopology.VanDerPauw, SampleGeometry.Empty, [new(1, PhysicalUnit.Ohm)]));
    }
    [Theory]
    [InlineData(1e300, 1e300, 1e-300)]
    [InlineData(1e-300, 1e-300, 1e300)]
    public void UnrepresentableHallGeometryDoesNotBecomeInfinityOrFakeZero(double r, double width, double length)
    {
        var result = SampleResistanceAnalysis.Calculate(SampleTopology.HallBar, new(new(width, PhysicalUnit.Meter), new(length, PhysicalUnit.Meter)), [new(r, PhysicalUnit.Ohm), new(1, PhysicalUnit.Ohm)]);
        Assert.Equal(SampleAnalysisIssues.UnrepresentableResult, result.Issues); Assert.Equal(2, result.Values.Count);
    }
    private static SampleAnalysisResult Vdp(double h, double v) => SampleResistanceAnalysis.Calculate(SampleTopology.VanDerPauw,
        new(thickness: new(1e-7, PhysicalUnit.Meter)), [new(h, PhysicalUnit.Ohm), new(-h, PhysicalUnit.Ohm), new(h, PhysicalUnit.Ohm), new(-h, PhysicalUnit.Ohm),
            new(v, PhysicalUnit.Ohm), new(-v, PhysicalUnit.Ohm), new(v, PhysicalUnit.Ohm), new(-v, PhysicalUnit.Ohm)]);
}

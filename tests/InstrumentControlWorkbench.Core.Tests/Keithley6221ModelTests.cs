using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class Keithley6221ModelTests
{
    [Theory]
    [InlineData(2e-9)]
    [InlineData(20e-9)]
    [InlineData(200e-9)]
    [InlineData(2e-6)]
    [InlineData(20e-6)]
    [InlineData(200e-6)]
    [InlineData(2e-3)]
    [InlineData(20e-3)]
    [InlineData(0.1)]
    public void EachNominalSpanAcceptsBothOverrangeSignsAndRejectsOverflow(double span)
    {
        var range = new Quantity(span, PhysicalUnit.Ampere); var limit = Math.Min(0.105, span * 1.05);
        Assert.Equal(range, new Keithley6221Settings(new(limit, PhysicalUnit.Ampere), new(105, PhysicalUnit.Volt), range).ManualRange);
        _ = new Keithley6221Settings(new(-limit, PhysicalUnit.Ampere), new(0.1, PhysicalUnit.Volt), range);
        Assert.Throws<ArgumentException>(() => new Keithley6221Settings(new(Math.BitIncrement(limit), PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), range));
    }
    [Theory]
    [InlineData(1e-12, 2e-9)]
    [InlineData(3e-9, 20e-9)]
    [InlineData(12e-3, 20e-3)]
    [InlineData(0.105, 0.1)]
    public void ImportRangeSelectorsResolveToNominalSpans(double selector, double expected)
        => Assert.Equal(new(expected, PhysicalUnit.Ampere), Keithley6221Settings.ResolveRange(new(selector, PhysicalUnit.Ampere)));
    [Theory]
    [InlineData("current")]
    [InlineData("complianceLow")]
    [InlineData("complianceHigh")]
    [InlineData("unit")]
    [InlineData("nonnominal")]
    [InlineData("rangeOverflow")]
    [InlineData("outputFraction")]
    [InlineData("autoZero")]
    public void InvalidSettingsAndActionsRejectAtTheDomainBoundary(string damage)
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            if (damage == "outputFraction") { Keithley6221Parameters.ValidateWrite(Keithley6221Parameters.Output.Id, new(0.5, PhysicalUnit.Dimensionless)); return; }
            if (damage == "autoZero") { Keithley6221Parameters.ValidateWrite(Keithley6221Parameters.EnableAutoRange.Id, new(0, PhysicalUnit.Dimensionless)); return; }
            _ = new Keithley6221Settings(new(damage == "current" ? 0.106 : 1e-5, damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Ampere),
                new(damage == "complianceLow" ? 0.09 : damage == "complianceHigh" ? 105.01 : 10, PhysicalUnit.Volt),
                damage == "nonnominal" ? new(5e-5, PhysicalUnit.Ampere) : damage == "rangeOverflow" ? new(2e-9, PhysicalUnit.Ampere) : null);
        });
    }
    [Fact]
    public void PureSettingsDoNotClaimSenseMeasuredCurrentOrSmuSnapshot()
    {
        var settings = new Keithley6221Settings(new(1e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt), new(20e-6, PhysicalUnit.Ampere));
        Assert.DoesNotContain(Keithley6221Parameters.All, p => p.Id.Value.Contains("measured", StringComparison.Ordinal));
        Assert.Equal(settings, settings.WithParameter(Keithley6221Parameters.Current.Id, settings.Current));
        Assert.Null(settings.WithParameter(Keithley6221Parameters.EnableAutoRange.Id, new(1, PhysicalUnit.Dimensionless)).ManualRange);
        Assert.Equal(new(0, PhysicalUnit.Dimensionless), settings.CaptureParameters()[Keithley6221Parameters.AutoRange.Id]);
        Assert.Throws<ArgumentException>(() => new K6221CurrentSourceSnapshot(true, false, null, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new K6221CurrentSourceSnapshot(false, true, settings, DateTimeOffset.UtcNow));
        Assert.False(typeof(ISourceMeterPort).IsAssignableFrom(typeof(IK6221CurrentSourcePort)));
    }
    [Fact]
    public void ModelSpecificProvenancePreservesK6221VoltageAndRejectsMeasuredSourceReference()
    {
        var config = new CurrentExcitationConfiguration(CurrentSourceModel.Keithley6221, new(1e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt), new(20e-6, PhysicalUnit.Ampere));
        var read = new ReadCurrentResistanceStep(new("read"), new("source"), new("meter"), CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere));
        var time = DateTimeOffset.UtcNow; var evidence = new IndependentResistanceEvidence(read, config, new(1e-5, PhysicalUnit.Ampere), new(0.001, PhysicalUnit.Volt), time, time, time, time);
        Assert.Equal(CurrentSourceModel.Keithley6221, evidence.SourceConfiguration.Model); Assert.Equal(100, evidence.SourceConfiguration.Compliance);
        var measured = new ReadCurrentResistanceStep(read.Id, read.Source, read.Meter, CurrentReference.MeasuredSourceReadback, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent);
        Assert.Throws<ArgumentException>(() => new IndependentResistanceEvidence(measured, config, evidence.Current, evidence.Voltage, time, time, time, time));
        Assert.Throws<ArgumentException>(() => new ExperimentPlan("duplicate", read, [new(new("source"), SourceFunction.Current, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.TwoWire, 1, true))], currentSourcePreparations: [new(new("source"), new(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))]));
    }
}

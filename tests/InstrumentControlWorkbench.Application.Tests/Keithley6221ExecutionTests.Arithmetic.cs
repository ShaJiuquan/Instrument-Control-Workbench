using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArithmeticExpectedExcitationUsesPersistedAxesForSuccessfulAndFailedArchiveRows(bool failed)
    {
        var folder = Path.Combine(Path.GetTempPath(), "arithmetic-current-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                await using var b = new Bench(archive); if (failed) b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "No meter frame");
                var value = new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("base_current")), new ConstantValue(new(2, PhysicalUnit.Dimensionless)));
                var template = b.Read; var read = new ReadCurrentResistanceStep(template.Id, template.Source, template.Meter, template.CurrentReference, template.VoltageColumn, template.CurrentColumn, template.ResistanceColumn, template.MinimumCurrent, value, retry: failed ? new(true, TimeSpan.Zero, true) : null);
                var plan = new ExperimentPlan("arithmetic excitation", new SequenceStep(new("root"), [new ForEachStep(new("loop"), new("base_current"), new ScanValues([new(.5e-5, PhysicalUnit.Ampere), new(1e-5, PhysicalUnit.Ampere)]), new SequenceStep(new("point"), [new SetParameterStep(new("set"), new(template.Source, Keithley6221Parameters.Current.Id), value), new SourceOutputStep(new("on"), template.Source, true), read]))]), currentSourcePreparations: [b.Preparation]);
                var loaded = new JsonExperimentPlanDocument().Parse(new JsonExperimentPlanDocument().Serialize(plan)); await b.Runs.StartPlanAsync(loaded, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(2, rows.Count);
                Assert.All(rows, row => { var expected = row.Values[new("base_current")].Value * 2; if (failed) Assert.Equal(expected, Assert.Single(row.AcquisitionFailures).SourceConfiguration.Level); else Assert.Equal(expected, Assert.Single(row.IndependentResistance).Current.Value); }); id = b.Runs.LatestDataSet.Id; Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            }
            await using var fresh = new FileRunArchive(folder); var actual = await fresh.LoadAsync(id, Token); Assert.Equal(2, actual.DataSet.Rows.Count); Assert.All(actual.DataSet.Rows, row => { if (failed) Assert.Single(row.AcquisitionFailures); else Assert.Single(row.IndependentResistance); });
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ArithmeticExpectedExcitationCannotDependOnUnpersistedVariables()
    {
        await using var b = new Bench(); var old = b.Read; var expression = new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("unpersisted")), new ConstantValue(new(2, PhysicalUnit.Dimensionless)));
        var read = new ReadCurrentResistanceStep(old.Id, old.Source, old.Meter, old.CurrentReference, old.VoltageColumn, old.CurrentColumn, old.ResistanceColumn, old.MinimumCurrent, expression);
        var plan = new ExperimentPlan("unpersisted", new SequenceStep(new("root"), [new AssignValueStep(new("seed"), new("unpersisted"), new ConstantValue(new(.5e-5, PhysicalUnit.Ampere))), new SourceOutputStep(new("on"), old.Source, true), read]), currentSourcePreparations: [b.Preparation]);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan)); Assert.Equal(0, b.Source.Connections); Assert.Equal(0, b.Source.Enables);
    }
}

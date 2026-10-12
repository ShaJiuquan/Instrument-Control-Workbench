using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanSampleCatalogFormTests
{
    [Theory]
    [InlineData(SampleTopology.TwoWire, false)]
    [InlineData(SampleTopology.FourWire, false)]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.FourWire, true)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task CalculatedSampleSaveReloadPreservesRoutingAndArchiveRevalidatesCopiedProvenance(SampleTopology topology, bool independent)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var original = b.Plan; var read = (ReadSampleResistanceStep)((SequenceStep)original.Root).Children[0];
        var calculation = new CalculatedMeasurementStep(new("calculation"), read, [new(new("scaled_R"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("R")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]);
        var plan = new ExperimentPlan(original.Name, new SequenceStep(new("root"), [calculation]), original.Preparations, original.Metadata, original.Routing, original.CurrentSourcePreparations, original.MaximumStepExecutions); var vm = owner.Plans!; vm.DocumentText = new JsonExperimentPlanDocument().Serialize(plan); vm.ValidateCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var folder = Path.Combine(Path.GetTempPath(), "calculated-sample-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            vm.DocumentPath = Path.Combine(folder, "plan.json"); await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(topology, independent); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(vm.DocumentPath, CancellationToken.None); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var snapshot = fresh.Runs.LatestDataSet!.Capture(); var raw = snapshot.Rows[0]; var result = snapshot.Rows[1]; Assert.Equal(2, snapshot.Rows.Count); Assert.NotNull(result.Calculation); Assert.Equal(raw.Routing!.Roles, result.Routing!.Roles); Assert.Equal(raw.Values[new("R")].Value * 2, result.Values[new("scaled_R")].Value); await fresh.AssertCleanup();
            await ArchiveCalculatedSnapshot(folder, snapshot);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task CalculatedAnalysisRetainsOriginalInputRangesAndDerivedUnitsOnFreshArchive(SampleTopology topology, bool independent)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var original = b.Plan; var template = ((ReadSampleResistanceStep)((SequenceStep)original.Root).Children[0]).Acquisition;
        var readings = SampleResistanceAnalysis.Selections(topology).Select((selection, index) =>
        {
            var id = new StepId("read" + index); var binding = new SampleRoutingBinding(new("primary"), new("wire"), selection);
            ResistanceAcquisitionStep acquisition = template is ReadCurrentResistanceStep current ? new ReadCurrentResistanceStep(id, current.Source, current.Meter, current.CurrentReference, current.VoltageColumn, current.CurrentColumn, current.ResistanceColumn, current.MinimumCurrent, current.ExpectedProgrammedCurrent, new(original.Routing!.Matrix, original.Routing.Resolve(binding).Closed)) : new ReadResistanceStep(id, template.Device, template.Function, template.VoltageColumn, template.CurrentColumn, template.ResistanceColumn, template.MinimumCurrent);
            return new ReadSampleResistanceStep(acquisition, binding, TimeSpan.Zero);
        });
        var analysis = new SampleAnalysisStep(new("analysis"), readings, SampleResistanceAnalysis.Kinds(topology).Select(kind => new SampleDerivedColumn(kind, new(kind.ToString()))));
        var calculation = new CalculatedMeasurementStep(new("calculated"), analysis, [new(new("double_sheet"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("SheetResistance")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]);
        var plan = new ExperimentPlan(original.Name, new SequenceStep(new("root"), [calculation]), original.Preparations, original.Metadata, original.Routing, original.CurrentSourcePreparations, original.MaximumStepExecutions); await b.Runs.StartPlanAsync(plan, CancellationToken.None); var snapshot = b.Runs.LatestDataSet!.Capture(); Assert.Equal(analysis.Readings.Count + 2, snapshot.Rows.Count); var raw = snapshot.Rows[^2]; var result = snapshot.Rows[^1]; Assert.Equal(raw.Analysis, result.Analysis); Assert.Equal(raw.Values[new("SheetResistance")].Value * 2, result.Values[new("double_sheet")].Value); Assert.Equal(PhysicalUnit.OhmPerSquare, result.Values[new("double_sheet")].Unit); await b.AssertCleanup();
        var folder = Path.Combine(Path.GetTempPath(), "calculated-analysis-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); try { await ArchiveCalculatedSnapshot(folder, snapshot); } finally { Directory.Delete(folder, true); }
    }
    private static async Task ArchiveCalculatedSnapshot(string folder, RunDataSetSnapshot snapshot)
    {
        await using (var archive = new FileRunArchive(folder))
        {
            await archive.OnStartedAsync(snapshot with { Rows = [], State = MeasurementRunState.Running, FinishedAt = null, Blackboard = null }, CancellationToken.None);
            foreach (var row in snapshot.Rows) await archive.OnRowAsync(snapshot.Id, row, CancellationToken.None);
            await archive.OnFinalizedAsync(snapshot, CancellationToken.None);
        }
        await using var fresh = new FileRunArchive(folder); var loaded = await fresh.LoadAsync(snapshot.Id, CancellationToken.None); Assert.Equal(snapshot.Rows.Count, loaded.DataSet.Rows.Count); Assert.Equal(snapshot.Rows[^1].Values, loaded.DataSet.Rows[^1].Values); Assert.Equal(snapshot.Rows[^1].Calculation!.InputRow, loaded.DataSet.Rows[^1].Calculation!.InputRow);
    }
}

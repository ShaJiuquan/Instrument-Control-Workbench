using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanValueFormTests
{
    [Fact]
    public void CalculationFormsPreserveBodyAndExpressionsAndReadonlyTemplateIsLazy()
    {
        var source = new InstrumentControlWorkbench.Infrastructure.Drivers.MockKeithley2450SourceMeter(); var body = new ReadStep(new("read"), [new(new("V"), new(source.Id, Keithley2450Parameters.SourceVoltage.Id))]); var original = new CalculatedMeasurementStep(new("calculated"), body, [new(new("double"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("V")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]); var calls = 0;
        var form = new PlanCalculatedMeasurementFormViewModel(original, [source.Descriptor], [], null, () => calls++, false); Assert.Equal(0, calls); Assert.Equal(original.Columns[0], form.Build().Columns[0]); form.IsReadOnly = true; Assert.True(form.BodyForm.IsReadOnly); Assert.True(form.Columns[0].Expression.IsReadOnly); Assert.False(form.AddColumnCommand.CanExecute(null));
        var view = new InstrumentControlWorkbench.App.Views.PlanCalculatedMeasurementFormView(); var template = Assert.Single(view.DataTemplates); Assert.False(template.Match(null)); Assert.True(template.Match(form.BodyForm)); Assert.IsType<InstrumentControlWorkbench.App.Views.PlanStepFormView>(template.Build(form.BodyForm));
    }
    [Fact]
    public async Task CalculatedPaletteAuthorsActualReadAndResultsSaveReloadAndRun()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "读取后计算"; var form = vm.PaletteForm!.CalculationForm!; var before = vm.DocumentText; Assert.Empty(form.Columns[0].ColumnId); vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        form.BodyForm.Acquisition!.SelectedSource = form.BodyForm.Acquisition.Sources.Single(source => source.Descriptor.Id == b.Source.Id); form.Columns[0].ColumnId = "new_power"; vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        var path = Path.Combine(Path.GetTempPath(), "calculated-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var rows = fresh.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); var result = rows[^1]; Assert.NotNull(result.Calculation); Assert.Equal(PhysicalUnit.Watt, result.Values[new("new_power")].Unit); Assert.Equal(rows[^2].Values[new("V")].Value * rows[^2].Values[new("I")].Value, result.Values[new("new_power")].Value); Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task NestedCalculationDraftLocksBusyCommandsAndIgnoresRetiredColumns()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "读取后计算"; var form = vm.PaletteForm!.CalculationForm!; var before = vm.DocumentText;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(form.IsReadOnly); Assert.True(form.BodyForm.IsReadOnly); Assert.True(form.Columns[0].Expression.IsReadOnly); Assert.False(form.AddColumnCommand.CanExecute(null)); vm.AppendNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); owner.IsBusy = false;
        var calls = 0; var read = new ReadStep(new("read"), [new(new("V"), new(b.Source.Id, new("k2450.source_voltage")))]); var local = new PlanCalculatedMeasurementFormViewModel(new(new("step"), read, [new(new("result"), new VariableValue(new("V")))]), b.Workspace.Devices, [], null, () => calls++, false); local.AddColumnCommand.Execute(null); var retired = local.Columns[^1]; local.RemoveColumnCommand.Execute(retired); var count = calls; retired.ColumnId = "retired"; retired.Expression.Variable = "late"; Assert.Equal(count, calls);
    }
}

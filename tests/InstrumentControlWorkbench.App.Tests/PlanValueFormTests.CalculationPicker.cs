using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanValueFormTests
{
    private static PlanCalculatedMeasurementFormViewModel ReadyCalculation(Bench b)
    {
        var vm = b.Shell.Plans!; vm.PaletteKind = "读取后计算"; var form = vm.PaletteForm!.CalculationForm!;
        form.BodyForm.Acquisition!.SelectedSource = form.BodyForm.Acquisition.Sources.Single(source => source.Descriptor.Id == b.Source.Id);
        form.Columns[0].ColumnId = "picker_power"; return form;
    }
    [Fact]
    public async Task CalculationPickerShowsOnlyFreshRowInputsAndElapsedWithoutUpstreamBlackboardValues()
    {
        var b = new Bench(); await using var owner = b.Shell; var form = ReadyCalculation(b);
        var inputs = form.Columns[0].Expression.Left!.VariablePicker.Choices;
        Assert.Equal(new[] { "I", "V", "elapsed" }, inputs.Select(input => input.Id)); Assert.DoesNotContain(inputs, input => input.Id is "bias" or "flag" or "number" or "voltage");
        Assert.Contains("Watt", form.Columns[0].PreviewSummary); Assert.DoesNotContain("可缺失", inputs.Single(input => input.Id == "V").DisplayName);
        Assert.False(form.HasPreviewMessage); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        var text = owner.Plans!.DocumentText; owner.Plans.AppendNodeCommand.Execute(null); Assert.False(owner.Plans.HasError, owner.Plans.Message); Assert.NotEqual(text, owner.Plans.DocumentText);
    }

    [Fact]
    public async Task DraftReadRenameRefreshesChoicesAndPreservesTheBrokenReferenceUntilExplicitSelection()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var form = ReadyCalculation(b); var left = form.Columns[0].Expression.Left!;
        form.BodyForm.Acquisition!.VoltageColumn = "renamed_voltage";
        Assert.DoesNotContain(left.VariablePicker.Choices, input => input.Id == "V"); Assert.Contains(left.VariablePicker.Choices, input => input.Id == "renamed_voltage"); Assert.Equal("V", left.Variable); Assert.Null(left.VariablePicker.SelectedChoice);
        Assert.Contains("待修正", form.Columns[0].PreviewSummary); var text = vm.DocumentText; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(text, vm.DocumentText);
        left.VariablePicker.SelectedChoice = left.VariablePicker.Choices.Single(input => input.Id == "renamed_voltage"); Assert.Contains("Watt", form.Columns[0].PreviewSummary);
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task EarlierResultAppearsOnlyInFollowingColumnsAndRenameDoesNotSilentlyRewriteReferences()
    {
        var b = new Bench(); await using var owner = b.Shell; var form = ReadyCalculation(b); form.AddColumnCommand.Execute(null); var second = form.Columns[1];
        second.ColumnId = "copied_power"; second.Expression.Kind = PlanValueInputKind.Variable; second.Expression.VariablePicker.SelectedChoice = second.Expression.VariablePicker.Choices.Single(input => input.Id == "picker_power");
        Assert.DoesNotContain(form.Columns[0].Expression.Left!.VariablePicker.Choices, input => input.Id == "picker_power"); Assert.Contains("Watt", second.PreviewSummary);
        form.Columns[0].ColumnId = "renamed_power"; Assert.Equal("picker_power", second.Expression.Variable); Assert.Null(second.Expression.VariablePicker.SelectedChoice); Assert.Contains("待修正", second.PreviewSummary);
        second.Expression.VariablePicker.SelectedChoice = second.Expression.VariablePicker.Choices.Single(input => input.Id == "renamed_power"); Assert.Contains("Watt", second.PreviewSummary);
        owner.Plans!.AppendNodeCommand.Execute(null); Assert.False(owner.Plans.HasError, owner.Plans.Message);
    }

    [Fact]
    public async Task RemovingAnEarlierResultInvalidatesDependentChoicesButRetainsUserText()
    {
        var b = new Bench(); await using var owner = b.Shell; var form = ReadyCalculation(b); var first = form.Columns[0]; form.AddColumnCommand.Execute(null); var second = form.Columns[1];
        second.ColumnId = "copy"; second.Expression.Kind = PlanValueInputKind.Variable; second.Expression.VariablePicker.SelectedChoice = second.Expression.VariablePicker.Choices.Single(input => input.Id == "picker_power");
        form.RemoveColumnCommand.Execute(first); Assert.Equal("picker_power", second.Expression.Variable); Assert.DoesNotContain(second.Expression.VariablePicker.Choices, input => input.Id == "picker_power");
        owner.Plans!.AppendNodeCommand.Execute(null); Assert.True(owner.Plans.HasError); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task InvalidBodyClearsCandidatesThenRestoresThemAfterRepairWithoutDiscardingFormula()
    {
        var b = new Bench(); await using var owner = b.Shell; var form = ReadyCalculation(b); var left = form.Columns[0].Expression.Left!;
        form.BodyForm.Acquisition!.VoltageColumn = ""; Assert.True(form.HasPreviewMessage); Assert.Empty(left.VariablePicker.Choices); Assert.Equal("V", left.Variable);
        form.BodyForm.Acquisition.VoltageColumn = "V"; Assert.False(form.HasPreviewMessage); Assert.Equal("V", left.VariablePicker.SelectedChoice!.Id); Assert.Contains("Watt", form.Columns[0].PreviewSummary);
    }

    [Fact]
    public async Task ReplacementNodeRefreshesPreviewWithoutMarkingAnUneditedNodeDirty()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; ReadyCalculation(b); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var step = ((SequenceStep)vm.ValidatedPlan!.Root).Children.OfType<CalculatedMeasurementStep>().Single(); vm.GraphNodes.Single(node => node.Id == step.Id).SelectCommand.Execute(null);
        var form = vm.NodeForm!.CalculationForm!; Assert.Contains("Watt", form.Columns[0].PreviewSummary); Assert.False(vm.NodeForm.IsDirty); Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); var left = form.Columns[0].Expression.Left!; Assert.True(left.VariablePicker.IsReadOnly);
        left.VariablePicker.SelectedChoice = left.VariablePicker.Choices.Single(input => input.Id == "I"); Assert.Equal("V", left.Variable); Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = false; vm.RefreshCommands();
    }

    [Fact]
    public async Task ChangingPaletteToALeafClearsContextAndReturningToContainerRetainsDraft()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var form = ReadyCalculation(b); var left = form.Columns[0].Expression.Left!;
        vm.GraphNodes.Single(node => node.Id.Value == "set").SelectCommand.Execute(null); Assert.Empty(left.VariablePicker.Choices); Assert.Equal("V", left.Variable); Assert.Same(form, vm.PaletteForm!.CalculationForm);
        vm.GraphNodes.Single(node => node.Id.Value == "root").SelectCommand.Execute(null); Assert.NotEmpty(left.VariablePicker.Choices); Assert.Equal("picker_power", form.Columns[0].ColumnId);
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
    }
    [Fact]
    public async Task PickerAuthoredOrderedResultsSaveReloadRunAndExportCompleteRowEvidence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "calculation-picker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var form = ReadyCalculation(b);
            form.BodyForm.Acquisition!.VoltageColumn = "actual_voltage";
            var left = form.Columns[0].Expression.Left!; left.VariablePicker.SelectedChoice = left.VariablePicker.Choices.Single(input => input.Id == "actual_voltage");
            foreach (var definition in new[] { (Id: "recovered_voltage", Input: "picker_power"), (Id: "picker_resistance", Input: "recovered_voltage") })
            {
                form.AddColumnCommand.Execute(null); var result = form.Columns[^1]; result.ColumnId = definition.Id;
                result.Expression.Kind = PlanValueInputKind.Arithmetic; result.Expression.SelectedOperator = ArithmeticOperator.Divide;
                result.Expression.Left!.Kind = PlanValueInputKind.Variable; result.Expression.Left.VariablePicker.SelectedChoice = result.Expression.Left.VariablePicker.Choices.Single(input => input.Id == definition.Input);
                result.Expression.Right!.Kind = PlanValueInputKind.Variable; result.Expression.Right.VariablePicker.SelectedChoice = result.Expression.Right.VariablePicker.Choices.Single(input => input.Id == "I");
            }
            Assert.Contains("Ohm", form.Columns[^1].PreviewSummary); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
            vm.DocumentPath = Path.Combine(folder, "plan.json"); await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var manager = new InstrumentManager(source, hub);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub, [archive]);
            var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs); var loaded = await workspace.LoadAsync(vm.DocumentPath, CancellationToken.None); await runs.StartPlanAsync(loaded.Plan, CancellationToken.None);
            var data = runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count); var raw = data.Rows[^2]; var row = data.Rows[^1];
            Assert.Equal(PhysicalUnit.Watt, row.Values[new("picker_power")].Unit); Assert.Equal(PhysicalUnit.Volt, row.Values[new("recovered_voltage")].Unit); Assert.Equal(PhysicalUnit.Ohm, row.Values[new("picker_resistance")].Unit);
            Assert.Equal(raw.Values[new("actual_voltage")].Value / raw.Values[new("I")].Value, row.Values[new("picker_resistance")].Value, 8);
            Assert.Equal(2, row.Calculation!.InputRow); Assert.All(row.Calculation.Outcomes, outcome => Assert.Equal(CalculationIssue.None, outcome.Issue)); Assert.False(runs.HasActiveRun); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var persisted = await fresh.LoadAsync(data.Id, CancellationToken.None); Assert.Equal(row.Values[new("picker_resistance")], persisted.DataSet.Rows[^1].Values[new("picker_resistance")]);
            var export = Path.Combine(folder, "export.csv"); await fresh.ExportCsvAsync(data.Id, export, CancellationToken.None); var csv = await File.ReadAllTextAsync(export); Assert.Contains("picker_power [Watt]", csv); Assert.Contains("picker_resistance [Ohm]", csv); Assert.Contains("calculation_json", csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

}

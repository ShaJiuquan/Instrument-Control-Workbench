using Avalonia.Controls;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.App.Views;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanValueFormTests
{
    [Fact]
    public async Task NodePickerUsesVerifiedPositionTypesAndDefinitionsWithoutDirtyingTheDocument()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var text = vm.DocumentText;
        vm.GraphNodes.Single(n => n.Id.Value == "set").SelectCommand.Execute(null); var value = vm.NodeForm!.ValueForm!;
        var choice = Assert.Single(value.VariablePicker.Choices); Assert.Equal("bias", choice.Id); Assert.Contains("Volt", choice.DisplayName); Assert.Contains("qualify", choice.DisplayName);
        Assert.False(vm.NodeForm.IsDirty); Assert.False(vm.NodeFragmentDirty); Assert.Equal(text, vm.DocumentText);
        value.Variable = "manual_id"; Assert.Null(value.VariablePicker.SelectedChoice); value.VariablePicker.SelectedChoice = choice;
        Assert.Equal("bias", value.Variable); vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task QualifiedNumbersAndArithmeticOperandsFilterOnlyValidOperandKinds()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "set").SelectCommand.Execute(null); var value = vm.NodeForm!.ValueForm!;
        value.Kind = PlanValueInputKind.QualifiedNumber; Assert.Equal("number", Assert.Single(value.VariablePicker.Choices).Id);
        value.VariablePicker.SelectedChoice = value.VariablePicker.Choices[0]; Assert.Equal(PhysicalUnit.Volt, ((QuantityFromNumberValue)value.Build()).Unit);
        value.Kind = PlanValueInputKind.Arithmetic; value.SelectedOperator = ArithmeticOperator.Add;
        var left = value.Left!; left.Kind = PlanValueInputKind.Variable; Assert.Equal(new[] { "bias", "number" }, left.VariablePicker.Choices.Select(c => c.Id));
        left.VariablePicker.SelectedChoice = left.VariablePicker.Choices.Single(c => c.Id == "bias"); Assert.Equal("bias", ((VariableValue)left.Build()).Variable.Value);
        Assert.DoesNotContain(left.VariablePicker.Choices, c => c.Id == "flag");
    }

    [Fact]
    public async Task ConditionPickerIncludesLogicalVariablesAndSynchronizesManualIdsWithoutInitialEdits()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "condition").SelectCommand.Execute(null); var condition = vm.NodeForm!.ConditionForm!;
        Assert.Equal(new[] { "bias", "flag", "number" }, condition.VariablePicker.Choices.Select(c => c.Id)); Assert.Equal("flag", condition.VariablePicker.SelectedChoice!.Id);
        Assert.False(vm.NodeForm.IsDirty); condition.Value.Kind = PlanValueInputKind.Variable; condition.Value.VariablePicker.SelectedChoice = condition.Value.VariablePicker.Choices.Single(c => c.Id == "flag");
        vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        Assert.IsType<VariableValue>(((ConditionStep)b.Workspace.ReadStep(vm.ValidatedPlan!, new("condition"))).Condition.Value);
    }

    [Fact]
    public async Task ChangedDeviceParameterClearsTheReferenceAndRefreshesUnitCompatibleChoices()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "set").SelectCommand.Execute(null); var form = vm.NodeForm!; Assert.Single(form.ValueForm!.VariablePicker.Choices);
        form.Address.SelectedParameter = form.Address.Parameters.Single(p => p.Descriptor.Id == Keithley2450Parameters.SourceCurrent.Id);
        Assert.Empty(form.ValueForm.Variable); Assert.Empty(form.ValueForm.VariablePicker.Choices); Assert.Null(form.ValueForm.VariablePicker.SelectedChoice);
    }

    [Fact]
    public async Task ReadOnlyAndRetiredPickersCannotApplyChangesToTheLiveNode()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "set").SelectCommand.Execute(null); var retired = vm.NodeForm!.ValueForm!; var oldChoice = retired.VariablePicker.Choices[0];
        vm.GraphNodes.Single(n => n.Id.Value == "condition").SelectCommand.Execute(null); var predicate = vm.NodeForm!.ConditionForm!;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(predicate.VariablePicker.IsReadOnly);
        predicate.VariablePicker.SelectedChoice = predicate.VariablePicker.Choices.Single(c => c.Id == "bias"); Assert.Equal("flag", predicate.Variable); Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = false; vm.RefreshCommands();
        retired.Variable = "stale"; retired.VariablePicker.SelectedChoice = oldChoice; Assert.False(vm.NodeFragmentDirty); Assert.False(vm.NodeForm!.IsDirty);
    }

    [Fact]
    public async Task PaletteUsesContainerEndContextAndPreservesDraftAcrossContextRefresh()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "变量赋值";
        vm.GraphNodes.Single(n => n.Id.Value == "root").SelectCommand.Execute(null); var form = vm.PaletteForm!; form.VariableName = "picked_copy";
        form.ValueForm!.Kind = PlanValueInputKind.Variable; var choice = form.ValueForm.VariablePicker.Choices.Single(c => c.Id == "bias"); form.ValueForm.VariablePicker.SelectedChoice = choice;
        // Zero-iteration while bodies do not guarantee their newly read columns outside.
        Assert.DoesNotContain(form.ValueForm.VariablePicker.Choices, c => c.Id == "voltage");
        vm.GraphNodes.Single(n => n.Id.Value == "loop-body").SelectCommand.Execute(null);
        Assert.Same(form, vm.PaletteForm); Assert.Equal("bias", form.ValueForm.Variable); Assert.Equal("picked_copy", form.VariableName);
        Assert.Contains(form.ValueForm.VariablePicker.Choices, c => c.Id == "voltage");
        vm.GraphNodes.Single(n => n.Id.Value == "root").SelectCommand.Execute(null);
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var appended = ((SequenceStep)vm.ValidatedPlan!.Root).Children[^1]; Assert.Equal("bias", ((VariableValue)((AssignValueStep)appended).Value).Variable.Value);
    }

    [Fact]
    public async Task PaletteReferenceBecomingUnavailableRetainsDraftAndRejectsAppendBeforeConnection()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "变量赋值";
        vm.GraphNodes.Single(n => n.Id.Value == "loop-body").SelectCommand.Execute(null); var form = vm.PaletteForm!; form.VariableName = "copy_read";
        form.ValueForm!.Kind = PlanValueInputKind.Variable; form.ValueForm.VariablePicker.SelectedChoice = form.ValueForm.VariablePicker.Choices.Single(c => c.Id == "voltage");
        vm.GraphNodes.Single(n => n.Id.Value == "root").SelectCommand.Execute(null); Assert.Equal("voltage", form.ValueForm.Variable); Assert.Null(form.ValueForm.VariablePicker.SelectedChoice);
        var text = vm.DocumentText; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(text, vm.DocumentText); Assert.Equal("voltage", form.ValueForm.Variable);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task PickedRecursiveExpressionSavesReloadsExecutesAndArchivesActualResult()
    {
        var folder = Path.Combine(Path.GetTempPath(), "picked-values-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!;
            vm.GraphNodes.Single(n => n.Id.Value == "seed").SelectCommand.Execute(null); vm.NodeForm!.ValueForm!.NumberText = ".02"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
            vm.GraphNodes.Single(n => n.Id.Value == "set").SelectCommand.Execute(null); var expression = vm.NodeForm!.ValueForm!;
            expression.Kind = PlanValueInputKind.Arithmetic; expression.SelectedOperator = ArithmeticOperator.Add;
            expression.Left!.Kind = PlanValueInputKind.Variable; expression.Left.VariablePicker.SelectedChoice = expression.Left.VariablePicker.Choices.Single(c => c.Id == "bias");
            expression.Right!.Kind = PlanValueInputKind.QualifiedNumber; expression.Right.Unit = PhysicalUnit.Volt; expression.Right.VariablePicker.SelectedChoice = expression.Right.VariablePicker.Choices.Single(c => c.Id == "number");
            vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); vm.DocumentPath = Path.Combine(folder, "plan.json"); await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var manager = new InstrumentManager(source, hub);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub, [archive]);
            var workspace = new ExperimentPlanWorkspace(new InstrumentControlWorkbench.Infrastructure.Plans.JsonExperimentPlanDocument(), runs);
            var loaded = await workspace.LoadAsync(vm.DocumentPath, CancellationToken.None); await runs.StartPlanAsync(loaded.Plan, CancellationToken.None);
            Assert.Equal(new Quantity(.04, PhysicalUnit.Volt), Assert.Single(runs.LatestDataSet!.Rows).Values[new("voltage")]); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(runs.HasActiveRun);
            await using var freshArchive = new FileRunArchive(Path.Combine(folder, "runs")); var persisted = await freshArchive.LoadAsync(runs.LatestDataSet.Id, CancellationToken.None);
            Assert.Equal(new Quantity(.04, PhysicalUnit.Volt), Assert.Single(persisted.DataSet.Rows).Values[new("voltage")]);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void EmptyUnknownContextAndForeignChoicesNeverInventOrOverwriteAReference()
    {
        var changed = 0; var value = new PlanValueFormViewModel(new VariableValue(new("manual")), () => changed++);
        Assert.Contains("尚无", value.VariablePicker.ContextHint); value.SetAvailableVariables([]); Assert.Empty(value.VariablePicker.Choices); Assert.Null(value.VariablePicker.SelectedChoice); Assert.Equal(0, changed);
        value.VariablePicker.SelectedChoice = new(new(new("foreign"), BlackboardValueType.Quantity(PhysicalUnit.Volt), false, [new("untrusted")]));
        Assert.Equal("manual", value.Variable); Assert.Equal(0, changed);
        Assert.IsAssignableFrom<UserControl>(new PlanVariablePickerView()); Assert.IsAssignableFrom<UserControl>(new PlanValueFormView()); Assert.IsAssignableFrom<UserControl>(new PlanConditionFormView());
    }
}

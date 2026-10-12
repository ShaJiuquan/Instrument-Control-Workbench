using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanGraphEditorTests
{
    [Fact]
    public async Task NodeDraftDisablesRunSaveAndOtherSelectionsUntilAtomicApplyOrDiscard()
    {
        var (shell, source) = Create(); await using var owner = shell; var vm = shell.Plans!;
        Assert.Equal(3, vm.GraphNodes.Count); var node = vm.GraphNodes.Single(n => n.Id.Value == "a"); node.SelectCommand.Execute(null);
        var original = vm.DocumentText; vm.NodeFragment = "invalid fragment";
        Assert.False(shell.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.False(vm.ValidateCommand.CanExecute(null)); Assert.True(vm.IsReadOnly);
        var path = Path.Combine(Path.GetTempPath(), "graph-draft-" + Guid.NewGuid().ToString("N") + ".json"); vm.DocumentPath = path;
        var draftMessage = vm.Message;
        vm.ValidateCommand.Execute(null); await vm.LoadCommand.ExecuteAsync(null); await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(File.Exists(path)); Assert.Equal(draftMessage, vm.Message); Assert.Equal(original, vm.DocumentText); Assert.True(vm.NodeFragmentDirty);
        vm.GraphNodes[0].SelectCommand.Execute(null); Assert.Same(node, vm.SelectedNode); vm.ApplyNodeCommand.Execute(null);
        Assert.True(vm.HasError); Assert.True(vm.NodeFragmentDirty); Assert.Equal(original, vm.DocumentText);
        vm.NodeFragment = "{\"id\":\"a\",\"kind\":\"delay\",\"seconds\":0.02}"; vm.ApplyNodeCommand.Execute(null);
        Assert.False(vm.NodeFragmentDirty); Assert.False(vm.HasError); Assert.True(shell.StartPlanCommand.CanExecute(null)); Assert.NotEqual(original, vm.DocumentText);
        Assert.Equal(TimeSpan.FromSeconds(.02), ((DelayStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[0]).Duration);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.NodeFragment = "bad"; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.NodeFragmentDirty); Assert.True(vm.IsValidated); Assert.False(vm.HasError);
    }
    [Theory]
    [InlineData("delay")]
    [InlineData("sequence")]
    [InlineData("repeat")]
    [InlineData("timeout")]
    [InlineData("retry")]
    public async Task PaletteAppendAndSequenceReorderRemoveUseValidatedPlanStructure(string kind)
    {
        var (shell, _) = Create(); await using var owner = shell; var vm = shell.Plans!; vm.PaletteKind = kind; vm.AppendNodeCommand.Execute(null);
        var sequence = (SequenceStep)vm.ValidatedPlan!.Root; var appended = sequence.Children[^1]; Assert.Equal(3, sequence.Children.Count);
        vm.GraphNodes.Single(n => n.Id == appended.Id).SelectCommand.Execute(null); vm.MoveEarlierCommand.Execute(null);
        sequence = (SequenceStep)vm.ValidatedPlan!.Root; Assert.Equal(appended.Id, sequence.Children[1].Id);
        vm.GraphNodes.Single(n => n.Id == appended.Id).SelectCommand.Execute(null); vm.RemoveNodeCommand.Execute(null);
        Assert.Equal(new[] { "a", "b" }, ((SequenceStep)vm.ValidatedPlan!.Root).Children.Select(n => n.Id.Value));
        Assert.True(vm.IsDirty); Assert.True(vm.IsValidated);
    }
    [Fact]
    public async Task ParentRunLockBlocksProgrammaticEditsAndStepFactsMarkActualGraphIds()
    {
        var (shell, _) = Create(); await using var owner = shell; var vm = shell.Plans!; var before = vm.DocumentText;
        shell.IsBusy = true; vm.RefreshCommands(); Assert.False(vm.AppendNodeCommand.CanExecute(null)); vm.AppendNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText);
        Assert.True(vm.IsNodeReadOnly); shell.IsBusy = false; vm.BeginRun(); vm.ApplyFact(new(WorkbenchNotificationKind.RunStarted, "run", RunId: "own"));
        vm.ApplyFact(new(WorkbenchNotificationKind.StepStarted, "a", RunId: "other", Step: new("a"))); Assert.DoesNotContain(vm.GraphNodes, n => n.IsActive);
        vm.ApplyFact(new(WorkbenchNotificationKind.StepStarted, "a", RunId: "own", Step: new("a"))); Assert.Equal("a", Assert.Single(vm.GraphNodes, n => n.IsActive).Id.Value);
        vm.ApplyFact(new(WorkbenchNotificationKind.RunCompleted, "finished", RunId: "own")); Assert.DoesNotContain(vm.GraphNodes, n => n.IsActive);
    }
    private static (MainWindowViewModel, MockKeithley2450SourceMeter) Create()
    {
        var source = new MockKeithley2450SourceMeter(); var hub = new NotificationHub(); var manager = new InstrumentManager(source, hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
        var plan = new ExperimentPlan("graph", new SequenceStep(new("root"), [new DelayStep(new("a"), TimeSpan.Zero), new DelayStep(new("b"), TimeSpan.Zero)]));
        return (new(manager, runs, hub, workspace, plan, "unused-graph.json"), source);
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanScopeIterationFormTests
{
    [Theory]
    [InlineData(IterationTiming.Immediate, IterationIndexBase.ZeroBased)]
    [InlineData(IterationTiming.Immediate, IterationIndexBase.OneBased)]
    [InlineData(IterationTiming.DelayBeforePoint, IterationIndexBase.ZeroBased)]
    [InlineData(IterationTiming.DelayBeforePoint, IterationIndexBase.OneBased)]
    public void IteratorRoundTripsBodyValuesUnitsTimingAndIndex(IterationTiming timing, IterationIndexBase index)
    {
        var body = new DelayStep(new("body"), TimeSpan.Zero); var step = new ForEachStep(new("loop"), new("value"), new([new(.05, timing == IterationTiming.Immediate ? PhysicalUnit.Volt : PhysicalUnit.Second)]), body, timing, index);
        var calls = 0; var parent = new PlanStepFormViewModel(step, [], () => calls++); Assert.True(parent.IsSupported); Assert.False(parent.IsDirty); Assert.Equal(0, calls);
        var built = (ForEachStep)parent.Build(); Assert.Same(body, built.Body); Assert.Equal(step.Values.Values, built.Values.Values); Assert.Equal(timing, built.Timing); Assert.Equal(index, built.IndexBase);
        parent.IterationForm!.ValuesText = "0.1, 0.2"; Assert.Equal(2, ((ForEachStep)parent.Build()).Values.Values.Count); Assert.True(parent.IsDirty);
        parent.IterationForm.Timing = timing == IterationTiming.Immediate ? IterationTiming.DelayBeforePoint : IterationTiming.Immediate; Assert.Empty(parent.IterationForm.ValuesText);
        Assert.Throws<FormatException>(() => parent.Build());
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1,,2")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("4294968")]
    public void InvalidTimedIterationValuesRejectBeforeAnyExecution(string text)
    {
        var form = new PlanIterationFormViewModel(new(new("loop"), new("delay"), new([new(0, PhysicalUnit.Second)]), new SequenceStep(new("body"), []), IterationTiming.DelayBeforePoint), () => { }) { ValuesText = text };
        Assert.ThrowsAny<Exception>(() => form.Build());
    }
    [Fact]
    public void ScopeMappingsPreserveChildrenAllowEmptyMapsAndKeepReadonlyMutationsInert()
    {
        var child = new AssignValueStep(new("child"), new("local"), new BooleanValue(true));
        var step = new SubtreeStep(new("scope"), [child], [new(new("parent"), new("local"))], [new(new("local"), new("export"))]);
        var calls = 0; var form = new PlanScopeFormViewModel(step, () => calls++); Assert.Equal(0, calls); Assert.Same(child, Assert.Single(form.Build().Children)); Assert.Equal(step.Inputs, form.Build().Inputs); Assert.Equal(step.Outputs, form.Build().Outputs);
        form.IsReadOnly = true; form.Inputs.AddCommand.Execute(null); form.Outputs.RemoveCommand.Execute(form.Outputs.Rows[0]); Assert.Single(form.Inputs.Rows); Assert.Single(form.Outputs.Rows); Assert.True(form.Inputs.Rows[0].IsReadOnly);
        form.IsReadOnly = false; form.Inputs.RemoveCommand.Execute(form.Inputs.Rows[0]); form.Outputs.RemoveCommand.Execute(form.Outputs.Rows[0]); Assert.Empty(form.Build().Inputs); Assert.Empty(form.Build().Outputs);
        form.Inputs.AddCommand.Execute(null); Assert.ThrowsAny<ArgumentException>(() => form.Build()); Assert.True(calls > 0);
    }
    [Theory]
    [InlineData("duplicateSource")]
    [InlineData("duplicateDestination")]
    [InlineData("unknownInput")]
    [InlineData("missingOutput")]
    [InlineData("outputType")]
    public async Task InvalidScopeDraftCannotReplacePlanOrConnectSources(string damage)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scope").SelectCommand.Execute(null); var before = vm.DocumentText; var form = vm.NodeForm!.ScopeForm!;
        if (damage.StartsWith("duplicate", StringComparison.Ordinal))
        {
            form.Inputs.AddCommand.Execute(null); form.Inputs.Rows[1].Source = damage == "duplicateSource" ? "parent_bias" : "alternate_bias"; form.Inputs.Rows[1].Destination = damage == "duplicateDestination" ? "local_bias" : "other_local";
        }
        else if (damage == "unknownInput") form.Inputs.Rows[0].Source = "missing";
        else if (damage == "missingOutput") form.Outputs.Rows[0].Source = "missing";
        else form.Outputs.Rows[0].Destination = "boolean_parent";
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError);
    }
    [Fact]
    public async Task ScopeInputRebindSavesFreshReloadAndExportsOnlySelectedValuesWithOutputOff()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scope").SelectCommand.Execute(null);
        vm.NodeForm!.ScopeForm!.Inputs.Rows[0].Source = "alternate_bias"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var path = Path.Combine(Path.GetTempPath(), "scope-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(); await using var freshOwner = fresh.Shell;
            var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows); Assert.Equal(new Quantity(.02, PhysicalUnit.Volt), row.Values[new("voltage")]);
            Assert.Equal(new BlackboardQuantity(new(.02, PhysicalUnit.Volt)), fresh.Runs.LatestBlackboard![new("exported")].Value);
            Assert.Equal(new BlackboardQuantity(new(.01, PhysicalUnit.Volt)), fresh.Runs.LatestBlackboard[new("parent_bias")].Value); Assert.DoesNotContain(new ParameterId("local_bias"), fresh.Runs.LatestBlackboard.Keys); Assert.DoesNotContain(new ParameterId("private_text"), fresh.Runs.LatestBlackboard.Keys);
            Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(IterationTiming.Immediate, IterationIndexBase.ZeroBased)]
    [InlineData(IterationTiming.DelayBeforePoint, IterationIndexBase.OneBased)]
    public async Task IterationValuesSaveFreshReloadAndProduceCorrectIndexAndTotal(IterationTiming timing, IterationIndexBase index)
    {
        var b = new Bench(iterator: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "iterator").SelectCommand.Execute(null); var form = vm.NodeForm!.IterationForm!;
        form.Timing = timing; form.IndexBase = index; form.ValuesText = timing == IterationTiming.Immediate ? "1,2,3" : "0,0.001,0.002"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var path = Path.Combine(Path.GetTempPath(), "iterator-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(iterator: true); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); Assert.Equal(3, fresh.Runs.LatestDataSet!.Rows.Count);
            Assert.Equal(Enumerable.Range((int)index, 3).Select(v => (double)v), fresh.Runs.LatestDataSet.Rows.Select(r => r.Values[new("item.index")].Value)); Assert.All(fresh.Runs.LatestDataSet.Rows, r => Assert.Equal(3, r.Values[new("item.total")].Value));
            Assert.All(fresh.Runs.LatestDataSet.Rows, r => Assert.Equal(timing == IterationTiming.Immediate ? PhysicalUnit.Dimensionless : PhysicalUnit.Second, r.Values[new("item")].Unit));
            Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ScopeChildAppendReorderAndRemovalPreserveMappingsAndCannotRemoveAnExportProducer()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scope").SelectCommand.Execute(null);
        Assert.True(vm.CanEditNode); Assert.True(vm.AppendNodeCommand.CanExecute(null)); vm.PaletteKind = "等待"; vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var subtree = (SubtreeStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[4]; Assert.Equal(4, subtree.Children.Count); Assert.Single(subtree.Inputs); Assert.Single(subtree.Outputs);
        var added = subtree.Children[^1].Id; vm.GraphNodes.Single(n => n.Id == added).SelectCommand.Execute(null); vm.MoveEarlierCommand.Execute(null); Assert.False(vm.HasError, vm.Message); vm.RemoveNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var before = vm.DocumentText; vm.GraphNodes.Single(n => n.Id.Value == "copy").SelectCommand.Execute(null); vm.RemoveNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Theory]
    [InlineData("数值迭代")]
    [InlineData("子流程")]
    public async Task PaletteCreatesUsableTypedContainers(string kind)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = kind; var before = vm.DocumentText;
        if (kind == "数值迭代") { vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.PaletteForm!.IterationForm!.Variable = "new_item"; vm.PaletteForm.IterationForm.ValuesText = "1,2"; }
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        if (kind == "子流程") { var added = ((SequenceStep)vm.ValidatedPlan!.Root).Children[^1]; vm.GraphNodes.Single(n => n.Id == added.Id).SelectCommand.Execute(null); Assert.True(vm.AppendNodeCommand.CanExecute(null)); }
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Fact]
    public async Task NestedMappingAndIterationDraftsKeepBusyMixedStaleAndPreparationGuards()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scope").SelectCommand.Execute(null); var stale = vm.NodeForm!.ScopeForm!;
        stale.Inputs.Rows[0].Source = "alternate_bias"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var applied = vm.DocumentText; stale.Inputs.Rows[0].Source = "missing"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.NodeForm!.ScopeForm!.Inputs.IsReadOnly); vm.NodeForm.ScopeForm.Inputs.AddCommand.Execute(null); Assert.Single(vm.NodeForm.ScopeForm.Inputs.Rows); vm.ApplyNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText);
        owner.IsBusy = false; vm.NodeForm.ScopeForm.Inputs.Rows[0].Source = "parent_bias"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(applied, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        vm.PreparationForm!.LevelText = ".01"; Assert.True(vm.NodeForm!.ScopeForm!.Inputs.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench(bool iterator = false)
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Runs = new(manager, new(TimeSpan.FromSeconds(5)), hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var read = new ReadStep(new("read"), [new(new("voltage"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id))]);
            var scope = new SubtreeStep(new("scope"), [new AssignValueStep(new("copy"), new("local_result"), new VariableValue(new("local_bias"))),
                new AssignValueStep(new("private"), new("private_text"), new TextValue("local")), new SetParameterStep(new("set"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id), new VariableValue(new("local_result")))],
                [new(new("parent_bias"), new("local_bias"))], [new(new("local_result"), new("exported"))]);
            var loop = new ForEachStep(new("iterator"), new("item"), new([new(1, PhysicalUnit.Dimensionless), new(2, PhysicalUnit.Dimensionless)]), read);
            var plan = new ExperimentPlan("scope iterator", new SequenceStep(new("root"), [new AssignValueStep(new("parent"), new("parent_bias"), new ConstantValue(new(.01, PhysicalUnit.Volt))),
                new AssignValueStep(new("alternate"), new("alternate_bias"), new ConstantValue(new(.02, PhysicalUnit.Volt))), new AssignValueStep(new("boolean"), new("boolean_parent"), new BooleanValue(true)),
                new SourceOutputStep(new("on"), Source.Id, true), .. iterator ? new ExperimentStep[] { loop } : new ExperimentStep[] { scope, read }]),
                [new(Source.Id, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))], maximumStepExecutions: 100);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-scope-iterator.json");
        }
    }
}

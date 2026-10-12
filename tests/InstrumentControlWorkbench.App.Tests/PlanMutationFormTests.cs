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

public sealed class PlanMutationFormTests
{
    [Theory]
    [InlineData(VariableMutation.Set)]
    [InlineData(VariableMutation.Add)]
    [InlineData(VariableMutation.Subtract)]
    [InlineData(VariableMutation.Multiply)]
    public void ExistingOperationsRoundTripTypedUnitsAndChangingOperationRequiresNewInput(VariableMutation action)
    {
        var step = new MutateValueStep(new("mutation"), new("bias"), action, new(.05, action == VariableMutation.Multiply ? PhysicalUnit.Dimensionless : PhysicalUnit.Volt));
        var calls = 0; var parent = new PlanStepFormViewModel(step, [], () => calls++); Assert.True(parent.IsSupported); Assert.False(parent.IsDirty); Assert.Equal(0, calls); Assert.Equal(step, parent.Build());
        parent.MutationForm!.Action = action == VariableMutation.Multiply ? VariableMutation.Add : VariableMutation.Multiply;
        Assert.Empty(parent.MutationForm.NumberText); Assert.Throws<FormatException>(() => parent.Build());
        parent.MutationForm.NumberText = "2";
        if (parent.MutationForm.Action == VariableMutation.Multiply) { parent.MutationForm.Unit = PhysicalUnit.Kelvin; Assert.False(parent.MutationForm.CanChooseUnit); Assert.Equal(PhysicalUnit.Dimensionless, ((MutateValueStep)parent.Build()).Value.Unit); }
        parent.IsReadOnly = true; Assert.True(parent.MutationForm.IsReadOnly); Assert.False(parent.MutationForm.CanChooseUnit);
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1,5")]
    [InlineData("")]
    public void OperationInputIsFiniteAndInvariant(string text)
    {
        var form = new PlanMutationFormViewModel(new(new("m"), new("x"), VariableMutation.Add, new(1, PhysicalUnit.Volt)), () => { }) { NumberText = text };
        Assert.Throws<FormatException>(() => form.Build());
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OutcomeCallbacksPreserveBodyIdsAndIndependentOptionalBranches(bool success, bool failure)
    {
        var body = new DelayStep(new("body"), TimeSpan.FromSeconds(.1));
        var step = new OutcomeMutationStep(new("hook"), body, success ? new(new("success"), new("x"), VariableMutation.Add, new(1, PhysicalUnit.Dimensionless)) : null,
            failure ? new(new("failure"), new("x"), VariableMutation.Subtract, new(.05, PhysicalUnit.Dimensionless)) : null);
        var calls = 0; var form = new PlanOutcomeMutationFormViewModel(step, () => calls++); var codec = new JsonExperimentPlanDocument();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("roundtrip", step))), JsonNode.Parse(codec.Serialize(new("roundtrip", form.Build()))))); Assert.Same(body, form.Build().Body); Assert.Equal(0, calls);
        Assert.Equal(!success, form.Success.IsReadOnly); Assert.Equal(!failure, form.Failure.IsReadOnly);
        form.UseSuccess = false; form.UseFailure = false; Assert.Null(form.Build().Success); Assert.Null(form.Build().Failure);
        form.UseSuccess = true;
        if (!success) { Assert.Empty(form.Success.Variable); Assert.Empty(form.Success.NumberText); Assert.Throws<FormatException>(() => form.Build()); form.Success.Variable = "counter"; form.Success.NumberText = "1"; }
        Assert.NotNull(form.Build().Success); Assert.Same(body, form.Build().Body); form.IsReadOnly = true; Assert.True(form.Success.IsReadOnly); Assert.True(form.Failure.IsReadOnly);
    }
    [Fact]
    public async Task EditedSuccessIncrementSavesFreshReloadAndControlsLoopWithOutputOff()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var path = Path.Combine(Path.GetTempPath(), "mutation-form-" + Guid.NewGuid().ToString("N") + ".json");
        vm.GraphNodes.Single(n => n.Id.Value == "hook").SelectCommand.Execute(null); var form = vm.NodeForm!.OutcomeForm!;
        form.Success.NumberText = "2"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var fresh = new Bench(); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None);
            Assert.Equal(2, fresh.Runs.LatestDataSet!.Rows.Count); Assert.Equal(new BlackboardQuantity(new(4, PhysicalUnit.Dimensionless)), fresh.Runs.LatestBlackboard![new("count")].Value);
            Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task FailureCallbackRunsForOrdinaryWorkflowFailureWhileRunRemainsFailed()
    {
        var b = new Bench(failedBody: true); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "hook").SelectCommand.Execute(null); vm.NodeForm!.OutcomeForm!.Failure.NumberText = "5"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(vm.ValidatedPlan!, CancellationToken.None));
        Assert.Equal(InstrumentControlWorkbench.Core.Measurements.MeasurementRunState.Faulted, b.Runs.LatestDataSet!.State);
        Assert.Equal(new BlackboardQuantity(new(5, PhysicalUnit.Dimensionless)), b.Runs.LatestBlackboard![new("count")].Value);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ArithmeticOverflowFaultDoesNotInvokeFailureCallbackAndStillStopsSource()
    {
        var b = new Bench(overflowBody: true); await using var owner = b.Shell;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(owner.Plans!.ValidatedPlan!, CancellationToken.None));
        Assert.Equal(InstrumentControlWorkbench.Core.Measurements.MeasurementRunState.Faulted, b.Runs.LatestDataSet!.State);
        Assert.Equal(new BlackboardQuantity(new(0, PhysicalUnit.Dimensionless)), b.Runs.LatestBlackboard![new("count")].Value);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("wrongUnit")]
    [InlineData("emptyVariable")]
    [InlineData("nonfinite")]
    public async Task InvalidCallbackDraftRetainsPlanAndPerformsNoIO(string damage)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "hook").SelectCommand.Execute(null); var before = vm.DocumentText;
        var form = vm.NodeForm!.OutcomeForm!.Success;
        if (damage == "wrongUnit") form.Unit = PhysicalUnit.Volt; else if (damage == "emptyVariable") form.Variable = ""; else form.NumberText = "NaN";
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError); Assert.True(vm.IsValidated);
    }
    [Theory]
    [InlineData("变量运算")]
    [InlineData("结果回调")]
    public async Task PaletteTemplatesRequireExplicitCallbackInputsAndUseTheCompiler(string kind)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; vm.PaletteKind = kind;
        if (kind == "变量运算")
        {
            vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
            vm.PaletteForm!.MutationForm!.Variable = "count"; vm.PaletteForm.MutationForm.NumberText = "7";
        }
        else
        {
            var callback = vm.PaletteForm!.OutcomeForm!; callback.UseSuccess = true; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
            callback.Success.Variable = "count"; callback.Success.NumberText = "2";
        }
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Fact]
    public async Task CallbackDraftsHonorStaleMixedBusyAndPreparationLocks()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "hook").SelectCommand.Execute(null);
        var stale = vm.NodeForm!.OutcomeForm!; stale.Success.NumberText = "2"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var applied = vm.DocumentText;
        stale.Success.NumberText = "3"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.NodeForm!.OutcomeForm!.Success.IsReadOnly); vm.NodeForm.OutcomeForm.Success.NumberText = "3"; vm.ApplyNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText);
        owner.IsBusy = false; vm.DiscardNodeCommand.Execute(null); vm.NodeForm!.OutcomeForm!.Success.NumberText = "3"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(applied, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        vm.PreparationForm!.LevelText = ".01"; Assert.True(vm.NodeForm!.OutcomeForm!.Success.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench(bool failedBody = false, bool overflowBody = false)
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Runs = new(manager, new(TimeSpan.FromSeconds(5)), hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            ExperimentStep body = overflowBody ? new MutateValueStep(new("body"), new("large"), VariableMutation.Multiply, new(2, PhysicalUnit.Dimensionless))
                : failedBody ? new ConditionStep(new("body"), new(new("count"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless)))) : new DelayStep(new("body"), TimeSpan.Zero);
            var hook = new OutcomeMutationStep(new("hook"), body, new(new("increment"), new("count"), VariableMutation.Add, new(1, PhysicalUnit.Dimensionless)), new(new("failure"), new("count"), VariableMutation.Add, new(10, PhysicalUnit.Dimensionless)));
            ExperimentStep loop = failedBody || overflowBody ? hook : new WhileStep(new("loop"), new(new("count"), ComparisonOperator.Less, new ConstantValue(new(3, PhysicalUnit.Dimensionless))),
                new SequenceStep(new("loop-body"), [new ReadStep(new("read"), [new(new("voltage"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id))]), hook]));
            var plan = new ExperimentPlan("mutation forms", new SequenceStep(new("root"), [new AssignValueStep(new("seed"), new("count"), new ConstantValue(new(0, PhysicalUnit.Dimensionless))),
                .. overflowBody ? new ExperimentStep[] { new AssignValueStep(new("large"), new("large"), new ConstantValue(new(double.MaxValue, PhysicalUnit.Dimensionless))) } : Array.Empty<ExperimentStep>(), new SourceOutputStep(new("on"), Source.Id, true), loop]),
                [new(Source.Id, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))], maximumStepExecutions: 80);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-mutation.json");
        }
    }
}

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

public sealed partial class PlanValueFormTests
{
    [Theory]
    [InlineData("quantity")]
    [InlineData("variable")]
    [InlineData("qualified")]
    [InlineData("boolean")]
    [InlineData("text")]
    public void AssignmentValuesRoundTripWithoutInitialDirtyNotifications(string kind)
    {
        PlanValue value = kind switch { "quantity" => new ConstantValue(new(.05, PhysicalUnit.Volt)), "variable" => new VariableValue(new("bias")), "qualified" => new QuantityFromNumberValue(new("number"), PhysicalUnit.Ampere), "boolean" => new BooleanValue(true), _ => new TextValue("试样\n\"A\" — µ") };
        var step = new AssignValueStep(new("assign"), new("destination"), value); var changed = 0;
        var form = new PlanStepFormViewModel(step, [], () => changed++); Assert.True(form.IsSupported); Assert.False(form.IsDirty); Assert.Equal(0, changed);
        Assert.Equal(step, form.Build()); form.VariableName = "new_destination"; Assert.Equal(value, ((AssignValueStep)form.Build()).Value); Assert.True(form.IsDirty);
    }
    [Theory]
    [InlineData("set")]
    [InlineData("wait")]
    [InlineData("branch")]
    [InlineData("while")]
    [InlineData("condition")]
    public void ExpressionAndConditionFormsPreserveBodiesVariablesUnitsAndEqualityTolerance(string kind)
    {
        var source = new MockKeithley2450SourceMeter(); var address = new ParameterAddress(source.Id, Keithley2450Parameters.SourceVoltage.Id);
        var condition = new ValueCondition(new("actual"), ComparisonOperator.Equal, new VariableValue(new("expected")), new(.0001, PhysicalUnit.Volt));
        var child = new DelayStep(new("body"), TimeSpan.Zero); var alternate = new DelayStep(new("other"), TimeSpan.FromSeconds(.1));
        ExperimentStep step = kind switch
        {
            "set" => new SetParameterStep(new("step"), address, new VariableValue(new("bias"))),
            "wait" => new WaitStableStep(new("step"), address, new VariableValue(new("target")), new(.01, PhysicalUnit.Volt), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(5)),
            "branch" => new IfElseStep(new("step"), condition, child, alternate),
            "while" => new WhileStep(new("step"), condition, child),
            _ => new ConditionStep(new("step"), condition),
        };
        var codec = new JsonExperimentPlanDocument(); var form = new PlanStepFormViewModel(step, [source.Descriptor], () => { });
        Assert.True(form.IsSupported); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("roundtrip", step))), JsonNode.Parse(codec.Serialize(new("roundtrip", form.Build())))));
        if (form.ConditionForm is { } predicate)
        {
            predicate.Variable = "new_actual"; var built = form.Build();
            if (built is IfElseStep branch) { Assert.Same(child, branch.Then); Assert.Same(alternate, branch.Else); }
            if (built is WhileStep loop) Assert.Same(child, loop.Body);
        }
    }
    [Fact]
    public void HardwareValuesCannotRelabelUnitsOrCarryAReferenceAcrossAChangedParameter()
    {
        var source = new MockKeithley2450SourceMeter(); var form = new PlanStepFormViewModel(new SetParameterStep(new("set"), new(source.Id, Keithley2450Parameters.SourceVoltage.Id), new ConstantValue(new(.01, PhysicalUnit.Volt))), [source.Descriptor], () => { });
        form.ValueForm!.Unit = PhysicalUnit.Kelvin; Assert.Equal(PhysicalUnit.Volt, ((ConstantValue)((SetParameterStep)form.Build()).Value).Value.Unit);
        form.ValueForm.Kind = PlanValueInputKind.Variable; form.ValueForm.Variable = "voltage";
        form.Address.SelectedParameter = form.Address.Parameters.Single(p => p.Descriptor.Id == Keithley2450Parameters.SourceCurrent.Id);
        Assert.Empty(form.ValueForm.Variable); Assert.Empty(form.ValueForm.NumberText); Assert.ThrowsAny<ArgumentException>(() => form.Build());
        form.ValueForm.Kind = PlanValueInputKind.Boolean; Assert.Throws<InvalidOperationException>(() => form.Build());
        form.ValueForm.Kind = PlanValueInputKind.QualifiedNumber; form.ValueForm.Variable = "number";
        Assert.Equal(PhysicalUnit.Ampere, ((QuantityFromNumberValue)((SetParameterStep)form.Build()).Value).Unit);
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1,5")]
    [InlineData("")]
    public void NumericInputIsFiniteAndInvariant(string text)
    {
        var form = new PlanValueFormViewModel(new ConstantValue(new(0, PhysicalUnit.Volt)), () => { }) { NumberText = text };
        Assert.Throws<FormatException>(() => form.Build());
    }
    [Fact]
    public void TextLengthAndConditionToleranceRemainBoundedWithoutLosingLiteralContent()
    {
        var form = new PlanValueFormViewModel(new TextValue(""), () => { }, allowLogical: true) { Text = new string('中', BlackboardText.MaximumLength) };
        Assert.Equal(BlackboardText.MaximumLength, ((TextValue)form.Build()).Value.Length); form.Text += "x"; Assert.Throws<ArgumentOutOfRangeException>(() => form.Build());
        var condition = new PlanConditionFormViewModel(new(new("left"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Volt))), () => { });
        condition.UseTolerance = true; condition.ToleranceText = "0"; Assert.Throws<FormatException>(() => condition.Build());
        condition.ToleranceText = "1e-6"; condition.ToleranceUnit = PhysicalUnit.Volt; Assert.Equal(new Quantity(1e-6, PhysicalUnit.Volt), condition.Build().EqualityTolerance);
        condition.IsReadOnly = true; Assert.True(condition.Value.IsReadOnly); Assert.False(condition.CanEditTolerance);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("wrongUnit")]
    [InlineData("physicalQualification")]
    [InlineData("booleanOrdering")]
    [InlineData("textMismatch")]
    [InlineData("toleranceUnit")]
    public async Task CompleteCompilerRejectsInvalidValueOrConditionDraftWithoutConnecting(string damage)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.GraphNodes.Single(n => n.Id.Value == (damage is "booleanOrdering" or "textMismatch" or "toleranceUnit" ? "condition" : "set")).SelectCommand.Execute(null);
        if (vm.NodeForm!.ConditionForm is { } condition)
        {
            if (damage == "booleanOrdering") condition.Comparison = ComparisonOperator.Greater;
            else if (damage == "textMismatch") { condition.Value.Kind = PlanValueInputKind.Text; condition.Value.Text = "true"; }
            else { condition.UseTolerance = true; condition.ToleranceText = ".01"; condition.ToleranceUnit = PhysicalUnit.Volt; }
        }
        else
        {
            var value = vm.NodeForm.ValueForm!; value.Kind = damage == "physicalQualification" ? PlanValueInputKind.QualifiedNumber : PlanValueInputKind.Variable;
            value.Variable = damage == "unknown" ? "missing" : damage == "wrongUnit" ? "flag" : "bias";
        }
        Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null)); vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError); Assert.True(vm.IsValidated);
    }
    [Fact]
    public async Task EditedNumericQualificationAssignmentAndWhileConditionSaveReloadExecuteAndCloseOutput()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var path = Path.Combine(Path.GetTempPath(), "value-form-" + Guid.NewGuid().ToString("N") + ".json");
        vm.GraphNodes.Single(n => n.Id.Value == "seed").SelectCommand.Execute(null); vm.NodeForm!.ValueForm!.NumberText = ".02"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        vm.GraphNodes.Single(n => n.Id.Value == "qualify").SelectCommand.Execute(null); Assert.Equal(PlanValueInputKind.QualifiedNumber, vm.NodeForm!.ValueForm!.Kind);
        vm.GraphNodes.Single(n => n.Id.Value == "loop").SelectCommand.Execute(null); var body = ((WhileStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[6]).Body;
        vm.NodeForm!.ConditionForm!.Comparison = ComparisonOperator.NotEqual; vm.NodeForm.ConditionForm.Value.Boolean = false;
        var builtBody = ((WhileStep)vm.NodeForm.Build()).Body;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(new JsonExperimentPlanDocument().Serialize(new("body", body))), JsonNode.Parse(new JsonExperimentPlanDocument().Serialize(new("body", builtBody)))));
        vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
            var fresh = new Bench(); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None);
            var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows); Assert.Equal(new Quantity(.02, PhysicalUnit.Volt), row.Values[new("voltage")]);
            Assert.Equal(new BlackboardBoolean(false), fresh.Runs.LatestBlackboard![new("flag")].Value); Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("变量赋值")]
    [InlineData("条件检查")]
    [InlineData("条件分支")]
    [InlineData("条件循环")]
    public async Task WorkflowPaletteUsesTypedAppendAndRejectsUnboundPredicates(string kind)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = kind; var before = vm.DocumentText;
        if (kind == "变量赋值") { vm.PaletteForm!.VariableName = "new_boolean"; vm.PaletteForm.ValueForm!.Kind = PlanValueInputKind.Boolean; vm.PaletteForm.ValueForm.Boolean = true; }
        else
        {
            vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
            vm.PaletteForm!.ConditionForm!.Variable = "flag"; vm.PaletteForm.ConditionForm.Value.Kind = PlanValueInputKind.Boolean; vm.PaletteForm.ConditionForm.Value.Boolean = false;
        }
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        Assert.NotEqual(before, vm.DocumentText);
    }
    [Fact]
    public async Task NestedValueDraftsHonorStaleMixedBusyAndPreparationLocks()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "seed").SelectCommand.Execute(null);
        var stale = vm.NodeForm!.ValueForm!; stale.NumberText = ".03"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var applied = vm.DocumentText;
        stale.NumberText = ".04"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.NodeForm!.ValueForm!.IsReadOnly); vm.NodeForm.ValueForm.NumberText = ".04"; vm.ApplyNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText);
        owner.IsBusy = false; vm.DiscardNodeCommand.Execute(null); vm.NodeForm!.ValueForm!.NumberText = ".04"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(applied, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        vm.PreparationForm!.LevelText = ".01"; Assert.True(vm.NodeForm!.ValueForm!.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench()
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Runs = new(manager, new(TimeSpan.FromSeconds(5)), hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var flag = new ValueCondition(new("flag"), ComparisonOperator.Equal, new BooleanValue(true));
            var plan = new ExperimentPlan("typed workflow", new SequenceStep(new("root"), [
                new AssignValueStep(new("seed"), new("number"), new ConstantValue(new(.01, PhysicalUnit.Dimensionless))),
                new AssignValueStep(new("qualify"), new("bias"), new QuantityFromNumberValue(new("number"), PhysicalUnit.Volt)),
                new AssignValueStep(new("flag"), new("flag"), new BooleanValue(true)),
                new SetParameterStep(new("set"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id), new VariableValue(new("bias"))),
                new ConditionStep(new("condition"), flag), new SourceOutputStep(new("on"), Source.Id, true),
                new WhileStep(new("loop"), flag, new SequenceStep(new("loop-body"), [new ReadStep(new("read"), [new(new("voltage"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id))]), new AssignValueStep(new("unset"), new("flag"), new BooleanValue(false))]))]),
                [new(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))], maximumStepExecutions: 80);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-value-form.json");
        }
    }
}

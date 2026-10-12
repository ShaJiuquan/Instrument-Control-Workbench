using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanValueFormTests
{
    [Fact]
    public void RecursiveArithmeticFormsPreserveOriginalTreeWithoutDirtyCallbacksAndPropagateReadonly()
    {
        var expression = new ArithmeticValue(ArithmeticOperator.Divide, new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("voltage")), new ConstantValue(new(2, PhysicalUnit.Dimensionless))), new VariableValue(new("current")));
        var calls = 0; var form = new PlanValueFormViewModel(expression, () => calls++); Assert.Equal(0, calls); Assert.Same(expression, form.Build()); Assert.True(form.HasArithmetic); Assert.Equal(ArithmeticOperator.Divide, form.SelectedOperator);
        Assert.False(form.Left!.Left!.HasArithmetic); Assert.DoesNotContain(PlanValueInputKind.Boolean, form.Left.Kinds); form.IsReadOnly = true; Assert.True(form.Left.IsReadOnly); Assert.True(form.Right!.IsReadOnly); Assert.True(form.Left.Right!.IsReadOnly); Assert.Equal(0, calls);
    }
    [Fact]
    public void NewArithmeticRequiresAuthoredOperatorAndOperandsAndRetiredChildCallbacksAreIgnored()
    {
        var calls = 0; var form = new PlanValueFormViewModel(new ConstantValue(new(1, PhysicalUnit.Volt)), () => calls++); form.Kind = PlanValueInputKind.Arithmetic; Assert.Null(form.SelectedOperator); Assert.Empty(form.Left!.NumberText); Assert.Empty(form.Right!.NumberText); Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedOperator = ArithmeticOperator.Add; Assert.Throws<FormatException>(() => form.Build()); form.Left.NumberText = "1"; form.Right.NumberText = "2"; form.Left.Unit = form.Right.Unit = PhysicalUnit.Volt; Assert.Equal(new Quantity(3, PhysicalUnit.Volt), NumericPlanValue.Evaluate(form.Build(), _ => throw new InvalidOperationException()));
        var retired = form.Left; form.Kind = PlanValueInputKind.Variable; var before = calls; retired.NumberText = "9"; Assert.Equal(before, calls); Assert.Null(form.Left);
    }
    [Fact]
    public void ChangedHardwareAddressClearsArithmeticAndNeverRelabelsItsOperands()
    {
        var unit = PhysicalUnit.Volt; var form = new PlanValueFormViewModel(new ConstantValue(new(1, unit)), () => { }, () => unit); form.Kind = PlanValueInputKind.Arithmetic; form.SelectedOperator = ArithmeticOperator.Multiply;
        form.Left!.NumberText = "2"; form.Left.Unit = PhysicalUnit.Ampere; form.Right!.NumberText = "3"; var built = form.Build(); Assert.Equal(PhysicalUnit.Ampere, NumericPlanValue.InferUnit(built, _ => throw new InvalidOperationException()));
        var retired = form.Left; unit = PhysicalUnit.Ampere; form.AddressChanged(); Assert.Null(form.SelectedOperator); Assert.NotSame(retired, form.Left); Assert.Empty(form.Left!.NumberText); Assert.Empty(form.Right!.NumberText); Assert.Throws<InvalidOperationException>(() => form.Build());
    }
    [Fact]
    public void MaximumUiDepthCannotCreateAnotherOperator()
    {
        PlanValue expression = new VariableValue(new("x")); for (var i = 1; i < 16; i++) expression = new ArithmeticValue(ArithmeticOperator.Add, expression, new ConstantValue(new(1, PhysicalUnit.Dimensionless)));
        var form = new PlanValueFormViewModel(expression, () => { }); var leaf = form; while (leaf.Left is { } child) leaf = child; Assert.DoesNotContain(PlanValueInputKind.Arithmetic, leaf.Kinds); leaf.Kind = PlanValueInputKind.Arithmetic; Assert.Throws<InvalidOperationException>(() => form.Build());
    }
    [Theory]
    [InlineData(ArithmeticOperator.Multiply, 2, PhysicalUnit.Dimensionless, .02)]
    [InlineData(ArithmeticOperator.Divide, 2, PhysicalUnit.Dimensionless, .005)]
    [InlineData(ArithmeticOperator.Add, .005, PhysicalUnit.Volt, .015)]
    public async Task EditedExpressionSavesReloadsAndDrivesActualSourceRead(ArithmeticOperator operation, double right, PhysicalUnit rightUnit, double expected)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(node => node.Id.Value == "qualify").SelectCommand.Execute(null);
        var value = vm.NodeForm!.ValueForm!; value.Kind = PlanValueInputKind.Arithmetic; value.SelectedOperator = operation; value.Left!.Kind = PlanValueInputKind.QualifiedNumber; value.Left.Variable = "number"; value.Left.Unit = PhysicalUnit.Volt;
        value.Right!.NumberText = right.ToString(System.Globalization.CultureInfo.InvariantCulture); value.Right.Unit = rightUnit; Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        var path = Path.Combine(Path.GetTempPath(), "arithmetic-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message); var fresh = new Bench(); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows); Assert.Equal(expected, row.Values[new("voltage")].Value, 12); Assert.Equal(PhysicalUnit.Volt, row.Values[new("voltage")].Unit); Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("unit")]
    [InlineData("unknown")]
    [InlineData("zero")]
    [InlineData("logical")]
    public async Task InvalidNestedFormCannotReplacePlanAndDiscardRestoresIt(string damage)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; vm.GraphNodes.Single(node => node.Id.Value == "qualify").SelectCommand.Execute(null); var value = vm.NodeForm!.ValueForm!; value.Kind = PlanValueInputKind.Arithmetic; value.SelectedOperator = damage == "zero" ? ArithmeticOperator.Divide : ArithmeticOperator.Add;
        value.Left!.Kind = PlanValueInputKind.Variable; value.Left.Variable = damage == "unknown" ? "missing" : damage == "logical" ? "flag" : "number"; value.Right!.NumberText = damage == "zero" ? "0" : "1"; value.Right.Unit = damage == "unit" ? PhysicalUnit.Volt : PhysicalUnit.Dimensionless;
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); vm.DiscardNodeCommand.Execute(null); Assert.False(vm.NodeFragmentDirty);
    }
    [Fact]
    public void OperandViewTemplateMatchesActualValuesAndDoesNotInstantiateForNullChildren()
    {
        var view = new InstrumentControlWorkbench.App.Views.PlanValueFormView(); var template = Assert.Single(view.DataTemplates);
        Assert.False(template.Match(null)); Assert.True(template.Match(new PlanValueFormViewModel(new ConstantValue(new(1, PhysicalUnit.Dimensionless)), () => { })));
        Assert.IsType<InstrumentControlWorkbench.App.Views.PlanValueFormView>(template.Build(new PlanValueFormViewModel(new VariableValue(new("value")), () => { })));
    }

}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static JsonObject FieldMeasurement() { var node = Node("measurement"); node["name"] = "measure_field"; return node; }
    private static JsonObject NumericChoice(string variable, string? op, double value)
    {
        var p = new JsonObject { ["variable"] = variable, ["value"] = value };
        if (op is not null) p["op"] = op;
        return Node("if_then_else", p, Node("then", null, FieldMeasurement()), Node("else", null, FieldMeasurement()));
    }

    [Theory]
    [InlineData("gt", 2, 1, true)]
    [InlineData(">", 1, 1, false)]
    [InlineData("lt", -2, -1, true)]
    [InlineData("<", 1, 1, false)]
    [InlineData("gte", 1, 1, true)]
    [InlineData(">=", 0, 1, false)]
    [InlineData("lte", 1, 1, true)]
    [InlineData("<=", 2, 1, false)]
    [InlineData("eq", 0.5e-9, 0, true)]
    [InlineData("==", 1e-9, 0, false)]
    [InlineData("eq", -1e-9, 0, false)]
    [InlineData("neq", 0.5e-9, 0, false)]
    [InlineData("!=", 1e-9, 0, true)]
    [InlineData(null, 2, 1, true)]
    public async Task PpmsNumericOperatorsPreserveStrictEpsilonAndDefaultGreater(string? op, double left, double right, bool then)
    {
        var b = new Bench(); var node = NumericChoice("iterator_value", op, right); node["params"]!["iterator_value"] = left;
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(document.Text).Plan, Token);
        Assert.True(b.Runs.LatestDataSet!.Rows.Single().Values.ContainsKey(new(then ? "ppms.root.0.0.value" : "ppms.root.1.0.value")));
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeNumericConditionsKeepExactComparisonUnlessToleranceIsExplicit(bool tolerant)
    {
        var b = new Bench(); var condition = new ValueCondition(new("value"), ComparisonOperator.Equal, new ConstantValue(Number(0)), tolerant ? Number(1e-9) : null);
        var plan = new ExperimentPlan("Native equality", new SequenceStep(new("root"), [Local("seed", "value", Number(0.5e-9)), new IfElseStep(new("branch"), condition, b.ReadField("then"), b.ReadField("else"))]));
        var native = b.Workspace.Inspect(plan); Assert.Equal(tolerant, native.Text.Contains("equalityTolerance", StringComparison.Ordinal));
        await b.Runs.StartPlanAsync(b.Workspace.Validate(native.Text).Plan, Token);
        Assert.True(b.Runs.LatestDataSet!.Rows.Single().Values.ContainsKey(new(tolerant ? "then.value" : "else.value")));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("unit")]
    [InlineData("operator")]
    public async Task InvalidEqualityToleranceRejectsBeforeDeviceAdmission(string invalid)
    {
        var b = new Bench(); var tolerance = invalid switch { "zero" => Number(0), "negative" => Number(-1), "unit" => new Quantity(1e-9, PhysicalUnit.Tesla), _ => Number(1e-9) };
        var condition = new ValueCondition(new("value"), invalid == "operator" ? ComparisonOperator.Less : ComparisonOperator.Equal, new ConstantValue(Number(0)), tolerance);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Powered(new SequenceStep(new("flow"), [Local("seed", "value", Number(0)), new ConditionStep(new("condition"), condition), b.ReadField("unreachable")]))));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("value", 1, false)]
    [InlineData("iterator_value", 1, false)]
    [InlineData("index", 0, true)]
    [InlineData("total", 2, false)]
    public async Task PureIteratorInjectsCurrentValueZeroBasedIndexAndTotal(string variable, double threshold, bool firstThen)
    {
        var b = new Bench(); var node = Node("for_each", new() { ["points"] = new JsonArray(-0.5, 2.5) }, NumericChoice(variable, "eq", threshold));
        var native = b.Workspace.Import(node.ToJsonString(), b.Bindings); var loop = Assert.IsType<ForEachStep>(native.Plan.Root);
        Assert.Equal(IterationIndexBase.ZeroBased, loop.IndexBase); Assert.Equal(new[] { -0.5, 2.5 }, loop.Values.Values.Select(q => q.Value));
        await b.Runs.StartPlanAsync(b.Workspace.Validate(native.Text).Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(2, rows.Count); Assert.Equal(new[] { 0.0, 1 }, rows.Select(r => r.Values[new("ppms.root.axis.index")].Value));
        Assert.True(rows[0].Values.ContainsKey(new(firstThen || variable == "total" ? "ppms.root.0.0.0.value" : "ppms.root.0.1.0.value")));
        Assert.Contains("(0-based)", b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("ppms.root.axis.index")).Parameter.Label);
    }

    [Fact]
    public async Task ExplicitIteratorInjectionOverridesDynamicContextAndMissingValueUsesPoint()
    {
        var b = new Bench(); var explicitIndex = NumericChoice("index", "eq", 9); explicitIndex["params"]!["index"] = 9;
        var pointComparedToItself = NumericChoice("value", "eq", 0); pointComparedToItself["params"]!.AsObject().Remove("value");
        var node = Node("for_each", new() { ["points"] = new JsonArray(0.5, 1.5) }, explicitIndex, pointComparedToItself);
        var imported = b.Workspace.Import(node.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(imported.Plan, Token);
        Assert.Equal(4, b.Runs.LatestDataSet!.Rows.Count);
        Assert.All(b.Runs.LatestDataSet.Rows, r => Assert.True(r.Values.ContainsKey(new("ppms.root.0.0.0.value")) || r.Values.ContainsKey(new("ppms.root.1.0.0.value"))));
    }

    [Fact]
    public async Task NestedIteratorsInjectOnlyDirectChildrenAndKeepDistinctCounters()
    {
        var b = new Bench(); var inner = Node("for_each", new() { ["points"] = new JsonArray(10.0, 20.0) }, NumericChoice("index", "eq", 0));
        var outer = Node("for_each", new() { ["points"] = new JsonArray(1.0, 2.0) }, inner, NumericChoice("index", "eq", 0));
        await b.Runs.StartPlanAsync(b.Workspace.Import(outer.ToJsonString(), b.Bindings).Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(6, rows.Count); Assert.Equal(new[] { 0.0, 1, 0, 1 }, rows.Where(r => r.Values.ContainsKey(new("ppms.root.0.axis.index"))).Select(r => r.Values[new("ppms.root.0.axis.index")].Value));
        Assert.False(rows[2].Values.ContainsKey(new("ppms.root.0.axis.index"))); Assert.True(rows[2].Values.ContainsKey(new("ppms.root.1.0.0.value"))); Assert.True(rows[5].Values.ContainsKey(new("ppms.root.1.1.0.value")));
        var hidden = Node("for_each", new() { ["points"] = new JsonArray(1.0) }, Node("sequence", null, NumericChoice("value", "gt", 0)));
        Assert.Throws<FormatException>(() => b.Workspace.Import(hidden.ToJsonString(), b.Bindings));
    }
}

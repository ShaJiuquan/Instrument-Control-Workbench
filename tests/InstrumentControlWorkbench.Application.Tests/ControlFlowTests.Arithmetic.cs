using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static ConstantValue ArithmeticConstant(double value, PhysicalUnit unit = PhysicalUnit.Dimensionless) => new(new(value, unit));
    [Theory]
    [InlineData("unknown")]
    [InlineData("logical")]
    [InlineData("unitMismatch")]
    [InlineData("qualification")]
    [InlineData("branch")]
    [InlineData("staticHardwareRange")]
    public async Task ArithmeticPreflightRejectsUndefinedLogicalMismatchedAndOutOfRangeOperandsWithoutIO(string damage)
    {
        var b = new Bench(); var seed = new AssignValueStep(new("seed"), new("x"), damage == "logical" ? new BooleanValue(true) : ArithmeticConstant(1, damage == "qualification" ? PhysicalUnit.Volt : PhysicalUnit.Dimensionless));
        PlanValue operand = damage == "qualification" ? new QuantityFromNumberValue(new("x"), PhysicalUnit.Volt) : new VariableValue(new(damage == "unknown" ? "missing" : "x"));
        var value = new ArithmeticValue(ArithmeticOperator.Add, operand, ArithmeticConstant(1, damage is "unitMismatch" or "qualification" ? PhysicalUnit.Volt : PhysicalUnit.Dimensionless));
        ExperimentStep tail = new AssignValueStep(new("calculation"), new("result"), value);
        ExperimentStep root = new SequenceStep(new("body"), [seed, tail]);
        if (damage == "branch") root = new SequenceStep(new("body"), [new AssignValueStep(new("flag"), new("flag"), new BooleanValue(true)), new IfElseStep(new("choice"), new(new("flag"), ComparisonOperator.Equal, new BooleanValue(true)), seed), tail]);
        if (damage == "staticHardwareRange") root = new SetParameterStep(new("write"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new ArithmeticValue(ArithmeticOperator.Add, ArithmeticConstant(200, PhysicalUnit.Volt), ArithmeticConstant(100, PhysicalUnit.Volt)));
        Assert.NotNull(Record.Exception(() => b.Runs.PreviewPlan(b.Powered(root)))); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData("force_success")]
    [InlineData("force_failure")]
    [InlineData("invert")]
    [InlineData("fallback")]
    [InlineData("retry")]
    [InlineData("timeout")]
    [InlineData("subtree")]
    [InlineData("branch_outcome")]
    [InlineData("numeric_branch")]
    [InlineData("while")]
    [InlineData("outcome_mutation")]
    [InlineData("threshold_switch")]
    public async Task DynamicDivisionFaultIsNeverConvertedToWorkflowSuccessAndClosesOutput(string wrapper)
    {
        var b = new Bench(); var denominator = new AssignValueStep(new("denominator"), new("denominator"), ArithmeticConstant(0));
        var invalid = new AssignValueStep(new("invalid"), new("result"), new ArithmeticValue(ArithmeticOperator.Divide, ArithmeticConstant(1), new VariableValue(new("denominator"))));
        var body = new SequenceStep(new("body"), [denominator, b.ReadSource("observed"), Wrap(wrapper, invalid, b.ReadField("alternative"))]);
        Assert.IsType<DivideByZeroException>(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(body), Token))); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.False(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow)); Assert.False(b.Runs.LatestBlackboard!.ContainsKey(new("result"))); Assert.False(b.Runs.LatestBlackboard.ContainsKey(new("alternative.value")));
    }
    [Fact]
    public async Task NestedArithmeticScanWritesConditionsMeasurementsAndPowerPersistAcrossFreshArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "arithmetic-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id; RunBlackboardSnapshot snapshot;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); var bias = new ArithmeticValue(ArithmeticOperator.Add, new VariableValue(new("requested")), ArithmeticConstant(.005, PhysicalUnit.Volt));
                var body = new SequenceStep(new("point"), [new SetParameterStep(new("set"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), bias), new ReadSourceSenseStep(new("paired"), b.Source.Id, SourceFunction.Voltage, new("V"), new("I")),
                    new AssignValueStep(new("resistance"), new("R"), new ArithmeticValue(ArithmeticOperator.Divide, new VariableValue(new("V")), new VariableValue(new("I")))),
                    new AssignValueStep(new("power"), new("P"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("V")), new VariableValue(new("I")))), new ConditionStep(new("check"), new(new("V"), ComparisonOperator.Equal, bias))]);
                var plan = b.Powered(new ForEachStep(new("loop"), new("requested"), new ScanValues([new(.01, PhysicalUnit.Volt), new(.02, PhysicalUnit.Volt), new(.03, PhysicalUnit.Volt)]), body));
                var path = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(path, new JsonExperimentPlanDocument().Serialize(plan), Token); var loaded = await b.Workspace.LoadAsync(path, Token); await b.Runs.StartPlanAsync(loaded.Plan, Token);
                var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); for (var i = 0; i < rows.Count; i++) Assert.Equal(new[] { .015, .025, .035 }[i], rows[i].Values[new("V")].Value, 12); var last = rows[^1]; snapshot = b.Runs.LatestBlackboardSnapshot!; id = snapshot.Id;
                Assert.Equal(new Quantity(last.Values[new("V")].Value / last.Values[new("I")].Value, PhysicalUnit.Ohm), ((BlackboardQuantity)snapshot.Entries[new("R")].Value).Value); Assert.Equal(new Quantity(last.Values[new("V")].Value * last.Values[new("I")].Value, PhysicalUnit.Watt), ((BlackboardQuantity)snapshot.Entries[new("P")].Value).Value);
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            }
            await using var fresh = new FileRunArchive(folder); var reloaded = await fresh.LoadAsync(id, Token); Assert.Equal(3, reloaded.DataSet.Rows.Count); Assert.Equal(snapshot.Entries[new("P")], reloaded.DataSet.Blackboard!.Entries[new("P")]); Assert.Equal(snapshot.Entries[new("R")], reloaded.DataSet.Blackboard.Entries[new("R")]);
            var export = Path.Combine(Path.GetTempPath(), "arithmetic-" + id.Value.ToString("N") + ".csv"); try { await fresh.ExportCsvAsync(id, export, Token); Assert.Contains("Watt", await File.ReadAllTextAsync(export)); } finally { File.Delete(export); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("zero")]
    [InlineData("logical")]
    [InlineData("unknownOperator")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("budget")]
    public void MalformedArithmeticDocumentIsRejected(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var node = JsonNode.Parse(codec.Serialize(new("arithmetic", new AssignValueStep(new("assign"), new("result"), ArithmeticConstant(0)))))!;
        JsonObject constant = new() { ["constant"] = new JsonObject { ["value"] = 1, ["unit"] = "Dimensionless" } };
        JsonObject arithmetic = new() { ["operator"] = damage == "unknownOperator" ? "RunScript" : damage == "zero" ? "Divide" : "Add", ["left"] = constant.DeepClone(), ["right"] = constant.DeepClone() };
        if (damage == "zero") arithmetic["right"]!["constant"]!["value"] = 0;
        if (damage == "logical") arithmetic["left"] = new JsonObject { ["boolean"] = true };
        if (damage == "extra") arithmetic["script"] = "ignored";
        if (damage == "missing") arithmetic.Remove("left");
        JsonNode expression = new JsonObject { ["arithmetic"] = arithmetic };
        if (damage == "budget") for (var i = 0; i < 16; i++) expression = new JsonObject { ["arithmetic"] = new JsonObject { ["operator"] = "Add", ["left"] = expression, ["right"] = constant.DeepClone() } };
        node["root"]!["value"] = expression; Assert.NotNull(Record.Exception(() => codec.Parse(node.ToJsonString())));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicArithmeticOverflowOrUnderflowRetainsRowAndClosesOutput(bool underflow)
    {
        var b = new Bench(); var value = new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("left")), new VariableValue(new("right")));
        var plan = b.Powered(new SequenceStep(new("body"), [new AssignValueStep(new("left"), new("left"), ArithmeticConstant(underflow ? 1e-300 : double.MaxValue)), new AssignValueStep(new("right"), new("right"), ArithmeticConstant(underflow ? 1e-300 : 2)), b.ReadSource("observed"), new AssignValueStep(new("calculation"), new("result"), value)]));
        Assert.IsType<ArithmeticException>(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token))); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task ConstantArithmeticSnapshotsRejectForgedResultAndUnit()
    {
        var b = new Bench(); var expression = new ArithmeticValue(ArithmeticOperator.Multiply, ArithmeticConstant(3, PhysicalUnit.Volt), ArithmeticConstant(2, PhysicalUnit.Ampere)); var plan = new ExperimentPlan("constant power", new AssignValueStep(new("calculate"), new("power"), expression));
        await b.Runs.StartPlanAsync(plan, Token); var snapshot = b.Runs.LatestBlackboardSnapshot!; var validator = new RunDataRowValidator(plan, b.Runs.LatestDataSet!.Schema); validator.ValidateBlackboard(snapshot.Id, snapshot);
        foreach (var forged in new[] { new Quantity(7, PhysicalUnit.Watt), new Quantity(6, PhysicalUnit.Volt) })
        {
            var old = snapshot.Entries[new("power")]; var entry = new BlackboardEntry(new BlackboardQuantity(forged), old.Source, old.UpdatedAt, old.Elapsed);
            var changed = new RunBlackboardSnapshot(snapshot.Id, snapshot.CapturedAt, snapshot.Elapsed, [KeyValuePair.Create(new ParameterId("power"), entry)]); Assert.Throws<ArgumentException>(() => validator.ValidateBlackboard(snapshot.Id, changed));
        }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task RepositoryArithmeticExampleLoadsAndRunsActualThreePointMeasurements()
    {
        var b = new Bench(); var document = await b.Workspace.LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "arithmetic-source-scan.plan.json"), Token); await b.Runs.StartPlanAsync(document.Plan, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); Assert.Equal(PhysicalUnit.Watt, ((BlackboardQuantity)b.Runs.LatestBlackboard![new("P")].Value).Value.Unit); Assert.Equal(PhysicalUnit.Ohm, ((BlackboardQuantity)b.Runs.LatestBlackboard[new("R")].Value).Value.Unit); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public void AggregateArithmeticDocumentBudgetRejectsBeforeAllocatingItsJsonTree()
    {
        PlanValue shared = ArithmeticConstant(0); for (var i = 0; i < 7; i++) shared = new ArithmeticValue(ArithmeticOperator.Add, shared, shared);
        var plan = new ExperimentPlan("large expressions", new SequenceStep(new("root"), Enumerable.Range(0, 258).Select(index => new AssignValueStep(new("step" + index), new("result"), shared))));
        var error = Assert.Throws<FormatException>(() => new JsonExperimentPlanDocument().Serialize(plan)); Assert.Contains("values exceed", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FoldedArithmeticRetainsFixedSourceSpanAndControllerLimits(bool controller)
    {
        var b = new Bench(); ExperimentPlan plan;
        if (controller)
        {
            var maximum = b.Field.Definition.Target.Maximum!.Value.Value;
            plan = new("controller expression", new SetParameterStep(new("set"), new(b.Field.Definition.Device.Id, b.Field.Definition.Target.Id), new ArithmeticValue(ArithmeticOperator.Add, ArithmeticConstant(maximum, PhysicalUnit.Tesla), ArithmeticConstant(1, PhysicalUnit.Tesla))));
        }
        else
        {
            var prepared = b.Powered(new SetParameterStep(new("set"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new ArithmeticValue(ArithmeticOperator.Add, ArithmeticConstant(.015, PhysicalUnit.Volt), ArithmeticConstant(.015, PhysicalUnit.Volt)))); var old = prepared.Preparations[0];
            plan = new(prepared.Name, prepared.Root, [new(old.Device, old.Function, old.Level, old.Compliance, old.Sense, new(.02, PhysicalUnit.Volt))]);
        }
        Assert.NotNull(Record.Exception(() => b.Runs.PreviewPlan(plan))); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

}

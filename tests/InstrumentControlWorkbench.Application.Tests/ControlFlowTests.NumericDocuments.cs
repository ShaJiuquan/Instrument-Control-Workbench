using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Theory]
    [InlineData("for_each", "{\"points\":{\"mode\":\"linear\",\"start\":0,\"end\":1,\"points\":2}}")]
    [InlineData("iterate", "{\"mode\":\"step\",\"start\":0,\"end\":1,\"step\":1}")]
    [InlineData("for_each", "{\"start\":0,\"end\":1,\"points\":2}")]
    [InlineData("iterate", "{\"points_mode\":\"step\",\"start\":0,\"end\":1,\"step\":1}")]
    public async Task PureIteratorAliasAndPointContractsGenerateSameRuntimeValues(string type, string p)
    {
        var b = new Bench(); var node = Node(type, JsonNode.Parse(p)!.AsObject(), NumericChoice("index", "eq", 0));
        var imported = b.Workspace.Import(node.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(imported.Plan, Token);
        Assert.Equal(new[] { 0.0, 1 }, b.Runs.LatestDataSet!.Rows.Select(r => r.Values[new("ppms.root.axis")].Value));
        Assert.True(b.Runs.LatestDataSet.Rows[0].Values.ContainsKey(new("ppms.root.0.0.0.value"))); Assert.True(b.Runs.LatestDataSet.Rows[1].Values.ContainsKey(new("ppms.root.0.1.0.value")));
    }

    [Theory]
    [InlineData("voltage")]
    [InlineData("bias")]
    public async Task VoltageScanPublishesBothTypedSourceAliases(string alias)
    {
        var b = new Bench(); var bindings = new ExperimentImportBindings(b.Bindings.Instruments, b.Bindings.Axes, b.Powered(new DelayStep(new("empty"), TimeSpan.Zero)).Preparations);
        var scan = Node("scan", new() { ["points"] = new JsonArray(0.0), ["settle_s"] = 0, ["channel"] = "none" }, NumericChoice(alias, "eq", 0)); scan["name"] = "scan_voltage";
        var document = b.Workspace.Import(scan.ToJsonString(), bindings);
        await b.Runs.StartPlanAsync(document.Plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.True(b.Runs.LatestDataSet.Rows[0].Values.ContainsKey(new("ppms.root.0.0.0.value")));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("field", PhysicalUnit.Tesla)]
    [InlineData("temperature", PhysicalUnit.Kelvin)]
    [InlineData("angle", PhysicalUnit.Degree)]
    [InlineData("current", PhysicalUnit.Ampere)]
    public async Task NumericAxisAliasesPreserveOperandAndToleranceUnits(string alias, PhysicalUnit unit)
    {
        var b = new Bench(); var imported = new PpmsExperimentPlanImporter().Parse(NumericChoice(alias, "eq", 1).ToJsonString(), b.Bindings);
        var branch = Assert.IsType<IfElseStep>(Assert.IsType<SequenceStep>(imported.Root).Children[1]);
        Assert.Equal(unit, Assert.IsType<ConstantValue>(branch.Condition.Value).Value.Unit); Assert.Equal(new Quantity(1e-9, unit), branch.Condition.EqualityTolerance);
        await b.Runs.StartPlanAsync(new("Typed aliases", new SequenceStep(new("root"), [Local("seed", "ppms.axis." + alias, new(1, unit)), imported.Root])), Token);
        Assert.True(b.Runs.LatestDataSet!.Rows.Single().Values.ContainsKey(new("ppms.root.0.0.value")));
    }

    [Theory]
    [InlineData("{\"points\":[]}")]
    [InlineData("{\"points\":null}")]
    [InlineData("{\"points\":[0],\"start\":0}")]
    [InlineData("{\"mode\":\"linear\",\"start\":0,\"end\":1,\"points\":1000001}")]
    [InlineData("{\"mode\":\"unknown\",\"start\":0,\"end\":1,\"points\":2}")]
    public async Task MalformedPureIteratorRejectsBeforeIo(string p)
    {
        var b = new Bench(); var node = Node("for_each", JsonNode.Parse(p)!.AsObject(), FieldMeasurement()); node["source_line"] = 13;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings)); Assert.Contains("source line 13", error.Message);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("99")]
    [InlineData("Bogus")]
    public void NativeIndexBaseRejectsNullAndUnknownNames(string? value)
    {
        var codec = new JsonExperimentPlanDocument(); var loop = new ForEachStep(new("loop"), new("value"), new([Number(1)]), new DelayStep(new("body"), TimeSpan.Zero));
        var node = JsonNode.Parse(codec.Serialize(new("Index", loop)))!.AsObject(); node["root"]!["indexBase"] = value;
        Assert.Throws<FormatException>(() => codec.Parse(node.ToJsonString()));
    }

    [Fact]
    public async Task FalseNumericConditionWithNoElseSucceedsWithoutCreatingMeasurement()
    {
        var b = new Bench(); var node = NumericChoice("iterator_value", "gt", 1); node["params"]!["iterator_value"] = 0; node["children"]!.AsArray().RemoveAt(1);
        await b.Runs.StartPlanAsync(b.Workspace.Import(node.ToJsonString(), b.Bindings).Plan, Token);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }

    [Fact]
    public async Task NumberAndTimeScanAliasesAreTypedAndRetainLastValueAfterScan()
    {
        var b = new Bench(); var number = Node("scan", new() { ["points"] = new JsonArray(1.0, 2.0) }, NumericChoice("scan_number", "eq", 1)); number["name"] = "scan_number";
        var time = Node("scan", new() { ["points"] = new JsonArray(0.0, 0.001) }, NumericChoice("scan_time", "eq", 0)); time["name"] = "scan_time";
        var root = Node("sequence", null, number, time, NumericChoice("scan_number", "eq", 2), NumericChoice("scan_time", "eq", 0.001));
        var document = b.Workspace.Import(root.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(document.Plan, Token); var data = b.Runs.LatestDataSet!;
        Assert.Equal(6, data.Rows.Count);
        Assert.True(data.Rows[0].Values.ContainsKey(new("ppms.root.0.0.0.0.value"))); Assert.True(data.Rows[1].Values.ContainsKey(new("ppms.root.0.0.1.0.value")));
        Assert.True(data.Rows[2].Values.ContainsKey(new("ppms.root.1.0.0.0.value"))); Assert.True(data.Rows[3].Values.ContainsKey(new("ppms.root.1.0.1.0.value")));
        Assert.True(data.Rows[4].Values.ContainsKey(new("ppms.root.2.0.0.value"))); Assert.True(data.Rows[5].Values.ContainsKey(new("ppms.root.3.0.0.value")));
        var loop = Assert.IsType<ForEachStep>(Assert.IsType<SequenceStep>(document.Plan.Root).Children[1]);
        var condition = Assert.IsType<IfElseStep>(Assert.IsType<SequenceStep>(Assert.IsType<SequenceStep>(loop.Body).Children[1]).Children[1]).Condition;
        Assert.Equal(PhysicalUnit.Second, Assert.IsType<ConstantValue>(condition.Value).Value.Unit);
    }

    [Fact]
    public async Task NestedSameAxisUpdatesAliasWithoutRestoringOlderPhysicalState()
    {
        var b = new Bench(); var inner = Node("scan", new() { ["points"] = new JsonArray(2.0) }, NumericChoice("scan_number", "eq", 2)); inner["name"] = "scan_number";
        var outer = Node("scan", new() { ["points"] = new JsonArray(1.0, 3.0) }, inner, NumericChoice("scan_number", "eq", 2)); outer["name"] = "scan_number";
        await b.Runs.StartPlanAsync(b.Workspace.Import(outer.ToJsonString(), b.Bindings).Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(4, rows.Count); Assert.True(rows[1].Values.ContainsKey(new("ppms.root.1.0.0.value"))); Assert.True(rows[3].Values.ContainsKey(new("ppms.root.1.0.0.value")));
        Assert.Equal(new[] { 1.0, 1, 3, 3 }, rows.Select(r => r.Values[new("ppms.root.axis")].Value));
    }

    [Fact]
    public async Task FieldAxisComparisonUsesTeslaAndCannotInventAnUnavailableAxis()
    {
        var b = new Bench(); var scan = Node("scan", new() { ["points"] = new JsonArray(0.0), ["wait_mode"] = "reached" }, NumericChoice("field", "eq", 0)); scan["name"] = "scan_field";
        var imported = b.Workspace.Import(scan.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(imported.Plan, Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.LatestDataSet.Rows[0].Values.ContainsKey(new("ppms.root.0.0.0.value")));
        var unavailable = new Bench();
        Assert.Throws<ArgumentException>(() => unavailable.Workspace.Import(NumericChoice("temperature", "gt", 300).ToJsonString(), unavailable.Bindings)); Assert.False((await unavailable.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("unknown_variable")]
    [InlineData("empty_variable")]
    [InlineData("operator")]
    [InlineData("missing_value")]
    [InlineData("null_value")]
    [InlineData("null_injection")]
    [InlineData("injection_text")]
    [InlineData("extra")]
    [InlineData("zero_children")]
    [InlineData("three_children")]
    public async Task NumericImportRejectsMalformedContractsWithSourceLocation(string damage)
    {
        var b = new Bench(); var node = NumericChoice("iterator_value", "gt", 0); node["source_line"] = 31; var p = node["params"]!.AsObject(); p["iterator_value"] = 1;
        switch (damage)
        {
            case "unknown_variable": p["variable"] = "mystery"; break;
            case "empty_variable": p["variable"] = ""; break;
            case "operator": p["op"] = "approximately"; break;
            case "missing_value": p.Remove("value"); break;
            case "null_value": p["value"] = null; break;
            case "null_injection": p["index"] = null; break;
            case "injection_text": p["total"] = "bad"; break;
            case "extra": p["unit"] = "Volt"; break;
            case "zero_children": node["children"] = new JsonArray(); break;
            case "three_children": node["children"]!.AsArray().Add(FieldMeasurement()); break;
        }
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings)); Assert.Contains("source line 31", error.Message);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("condition")]
    [InlineData("if_else")]
    public void NativeConditionRejectsExplicitNullTolerance(string kind)
    {
        var codec = new JsonExperimentPlanDocument(); var condition = new ValueCondition(new("value"), ComparisonOperator.Equal, new ConstantValue(Number(0)));
        ExperimentStep step = kind == "condition" ? new ConditionStep(new("check"), condition) : new IfElseStep(new("branch"), condition, new DelayStep(new("then"), TimeSpan.Zero));
        var node = JsonNode.Parse(codec.Serialize(new("Malformed", step)))!.AsObject(); var target = kind == "condition" ? node["root"]! : node["root"]!["condition"]!;
        target["equalityTolerance"] = null; Assert.Throws<FormatException>(() => codec.Parse(node.ToJsonString()));
    }

    [Theory]
    [InlineData("ZeroBased")]
    [InlineData("OneBased")]
    [InlineData("omitted")]
    public async Task NativeIteratorIndexChoiceRoundTripsAndDefaultsToOne(string setting)
    {
        var b = new Bench(); var loop = new ForEachStep(new("loop"), new("number"), new([Number(1), Number(2)]), b.ReadField("value"));
        var node = JsonNode.Parse(b.Workspace.Inspect(new("Index", loop)).Text)!.AsObject(); if (setting != "omitted") node["root"]!["indexBase"] = setting;
        var plan = b.Workspace.Validate(node.ToJsonString()).Plan; await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(setting == "ZeroBased" ? new[] { 0.0, 1 } : [1.0, 2], b.Runs.LatestDataSet!.Rows.Select(r => r.Values[new("number.index")].Value));
    }

    [Fact]
    public async Task ImportedNumericIteratorPauseAndAbortPreventsFollowingPoints()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var condition = Node("if_then_else", new() { ["variable"] = "value", ["value"] = 0 }, Node("then", null, Node("delay", new() { ["delay_s"] = 30 }), FieldMeasurement()), Node("else", null, FieldMeasurement()));
        var iterator = Node("for_each", new() { ["points"] = new JsonArray(1.0, 2.0) }, condition);
        var imported = b.Workspace.Import(iterator.ToJsonString(), b.Bindings); var run = b.Runs.StartPlanAsync(b.Powered(imported.Plan.Root), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("ppms.root.0.0.0.wait")); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task NumericFixtureNativeArchiveCsvAndFreshReplayRetainBranchAndIteratorSemantics()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"numeric-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var imported = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-numeric-conditions-import.json"), b.Bindings, Token);
                var file = Path.Combine(folder, "native.json"); await b.Workspace.SaveAsync(file, imported.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token); var data = b.Runs.LatestDataSet!; id = data.Id;
                Assert.Equal(5, data.Rows.Count); Assert.All(data.Rows, data.Schema.ValidateRow);
                Assert.Equal(new[] { 0.0, 1, 2 }, data.Rows.Take(3).Select(r => r.Values[new("ppms.root.0.axis.index")].Value));
                Assert.All(data.Rows.Take(3), row => Assert.True(row.Values.ContainsKey(new("ppms.root.0.0.0.0.value"))));
                Assert.Equal(7, data.Plan!.Metadata["ppms.root.0.0.source_line"].Value);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(id, Token); Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State);
            var csv = Path.Combine(folder, "numeric.csv"); await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("ZeroBased", text); Assert.Contains("equalityTolerance", text); Assert.Contains("quantityFromNumber", text); Assert.Contains("quality_json", text);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Equal(5, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

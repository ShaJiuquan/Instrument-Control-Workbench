using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static JsonObject Callback(string variable, string action, double value) => new() { ["variable"] = variable, ["action"] = action, ["value"] = value };
    private static JsonObject CounterSeed() { var seed = Node("delay", new() { ["delay_s"] = 0 }); seed["_onSuccess"] = Callback("count", "set", 0); return seed; }

    [Theory]
    [InlineData(127, false, false)]
    [InlineData(128, false, false)]
    [InlineData(128, true, false)]
    [InlineData(128, false, true)]
    public async Task MaximumDepthImportRemainsBoundedForValidInvalidAndGuardedTrees(int depth, bool invalid, bool guarded)
    {
        var b = new Bench();
        var root = invalid ? Node("unsupported") : Node("command", new() { ["seconds"] = 0 });
        if (!invalid) root["name"] = "sleep";
        for (var index = 0; index < depth; index++)
        {
            root = Node("sequence", null, root);
            if (guarded) root["_skipIf"] = new JsonObject { ["variable"] = "field", ["value"] = 0 };
        }
        var text = root.ToJsonString(new() { MaxDepth = 512 });
        if (invalid || guarded) Assert.Throws<FormatException>(() => b.Workspace.Import(text, b.Bindings));
        else Assert.Equal(depth + 1, b.Workspace.Import(text, b.Bindings).Preview.MaximumStepExecutions);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task PpmsWhileReevaluatesCustomVariableAndSuccessCallbackUntilFalse()
    {
        var b = new Bench(); var child = FieldMeasurement(); child["_onSuccess"] = Callback("count", "add", 1);
        var loop = Node("while", new() { ["variable"] = "count", ["value"] = 3 }, child); loop["source_line"] = 9;
        var root = Node("sequence", null, CounterSeed(), loop, NumericChoice("count", "eq", 3));
        var imported = b.Workspace.Import(root.ToJsonString(), b.Bindings); Assert.True(imported.Preview.HasDynamicLoops);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(imported.Text).Plan, Token); var data = b.Runs.LatestDataSet!;
        Assert.Equal(4, data.Rows.Count); Assert.Equal(new[] { 1.0, 2, 3 }, data.Rows.Take(3).Select(r => r.Values[new("ppms.root.1.iteration")].Value));
        Assert.True(data.Rows[^1].Values.ContainsKey(new("ppms.root.2.0.0.value"))); Assert.False(data.Rows[^1].Values.ContainsKey(new("ppms.root.1.iteration")));
        Assert.Equal(9, data.Plan!.Metadata["ppms.root.1.source_line"].Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PpmsCallbacksChooseOrdinaryOutcomeWithoutChangingIt(bool success)
    {
        var b = new Bench(); var controlled = Node(success ? "force_success" : "force_failure", null, FieldMeasurement());
        controlled["_onSuccess"] = Callback("count", "add", 1); controlled["_onFailure"] = Callback("count", "add", 2);
        var handled = success ? controlled : Node("force_success", null, controlled);
        var root = Node("sequence", null, CounterSeed(), handled, NumericChoice("count", "eq", success ? 1 : 2));
        await b.Runs.StartPlanAsync(b.Workspace.Import(root.ToJsonString(), b.Bindings).Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(2, rows.Count); Assert.True(rows[^1].Values.ContainsKey(new("ppms.root.2.0.0.value")));
    }

    [Theory]
    [InlineData("skip", 1)]
    [InlineData("failure_first", 1)]
    [InlineData("execute", 2)]
    public async Task PpmsPreconditionsRespectPriorityAndDoNotRunCallbacksForEarlySkipOrFailure(string mode, int rows)
    {
        var b = new Bench(); var node = FieldMeasurement(); node["_onSuccess"] = Callback("count", "add", 1); node["_onFailure"] = Callback("count", "add", 9);
        node["_skipIf"] = new JsonObject { ["variable"] = "count", ["value"] = mode == "execute" ? 1 : 0 };
        if (mode == "failure_first") node["_failureIf"] = new JsonObject { ["variable"] = "count", ["value"] = 0 };
        var handled = mode == "failure_first" ? Node("force_success", null, node) : node;
        var root = Node("sequence", null, CounterSeed(), handled, NumericChoice("count", "eq", mode == "execute" ? 1 : 0));
        await b.Runs.StartPlanAsync(b.Workspace.Import(root.ToJsonString(), b.Bindings).Plan, Token);
        Assert.Equal(rows, b.Runs.LatestDataSet!.Rows.Count); Assert.True(b.Runs.LatestDataSet.Rows[^1].Values.ContainsKey(new("ppms.root.2.0.0.value")));
    }

    [Theory]
    [InlineData("_onSuccess", "{\"variable\":\"count\",\"action\":\"divide\",\"value\":1}")]
    [InlineData("_onFailure", "{\"variable\":\"count\",\"value\":null}")]
    [InlineData("_onSuccess", "{\"variable\":\"bad name\",\"value\":1}")]
    [InlineData("_skipIf", "{\"variable\":\"count\",\"op\":\"unknown\",\"value\":1}")]
    [InlineData("_failureIf", "{\"variable\":\"count\",\"value\":1,\"extra\":true}")]
    public async Task MalformedPpmsConditionsAndCallbacksRejectWithLocationBeforeIo(string key, string value)
    {
        var b = new Bench(); var node = FieldMeasurement(); node[key] = JsonNode.Parse(value); node["source_line"] = 23;
        var root = Node("sequence", null, CounterSeed(), node);
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(root.ToJsonString(), b.Bindings)); Assert.Contains("source line 23", error.Message);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("condition_missing")]
    [InlineData("body_missing")]
    [InlineData("tolerance_null")]
    [InlineData("hook_null")]
    [InlineData("hook_wrong_kind")]
    [InlineData("budget_null")]
    [InlineData("budget_zero")]
    [InlineData("budget_large")]
    [InlineData("budget_fraction")]
    public void NativeLoopHooksAndBudgetRejectMalformedFields(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); ExperimentStep step = damage.StartsWith("hook", StringComparison.Ordinal) ? new OutcomeMutationStep(new("hook"), new DelayStep(new("body"), TimeSpan.Zero)) : new WhileStep(new("loop"), Below("count", 1), new DelayStep(new("body"), TimeSpan.Zero));
        var node = JsonNode.Parse(codec.Serialize(new("Malformed", step)))!.AsObject(); var root = node["root"]!.AsObject();
        switch (damage)
        {
            case "condition_missing": root.Remove("condition"); break;
            case "body_missing": root.Remove("body"); break;
            case "tolerance_null": root["condition"]!["equalityTolerance"] = null; break;
            case "hook_null": root["success"] = null; break;
            case "hook_wrong_kind": root["failure"] = root["body"]!.DeepClone(); break;
            case "budget_null": node["maximumStepExecutions"] = null; break;
            case "budget_zero": node["maximumStepExecutions"] = 0; break;
            case "budget_large": node["maximumStepExecutions"] = 10000001; break;
            case "budget_fraction": node["maximumStepExecutions"] = 1.5; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(node.ToJsonString()));
    }

    [Fact]
    public async Task WhileFixtureNativeArchiveCsvFreshReplayPreservesAllIterationRows()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"while-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var imported = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-while-callbacks-import.json"), b.Bindings, Token);
                var plan = new ExperimentPlan(imported.Plan.Name, imported.Plan.Root, imported.Plan.Preparations, imported.Plan.Metadata, imported.Plan.Routing, imported.Plan.CurrentSourcePreparations, 30);
                var native = b.Workspace.Inspect(plan); var file = Path.Combine(folder, "native.json"); await b.Workspace.SaveAsync(file, native.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token); var data = b.Runs.LatestDataSet!; id = data.Id;
                Assert.Equal(4, data.Rows.Count); Assert.Equal(30, data.Plan!.MaximumStepExecutions); Assert.All(data.Rows, data.Schema.ValidateRow);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(id, Token);
            Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State); Assert.Equal(30, saved.DataSet.Plan!.MaximumStepExecutions);
            var csv = Path.Combine(folder, "loop.csv"); await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("iteration", text); Assert.Contains("outcome_mutation", text); Assert.Contains("maximumStepExecutions", text); Assert.Contains("quality_json", text);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan, Token); Assert.Equal(4, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

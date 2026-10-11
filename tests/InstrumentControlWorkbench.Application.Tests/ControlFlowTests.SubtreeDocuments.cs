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
    [Theory]
    [InlineData("subtree")]
    [InlineData("scope")]
    [InlineData("isolated")]
    public async Task PpmsScopeAliasesAllowOmittedMappingsAndPreserveSourceLine(string type)
    {
        var b = new Bench(); var child = Node("measurement"); child["name"] = "measure_field";
        var node = Node(type, null, child); node["source_line"] = 7;
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings);
        var scope = Assert.IsType<SubtreeStep>(document.Plan.Root); Assert.Empty(scope.Inputs); Assert.Empty(scope.Outputs);
        Assert.Equal(7, document.Plan.Metadata["ppms.root.source_line"].Value);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(document.Text).Plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Theory]
    [InlineData("{\"inputs\":null}")]
    [InlineData("{\"outputs\":[]}")]
    [InlineData("{\"inputs\":{\"one\":5}}")]
    [InlineData("{\"outputs\":{\"one\":null}}")]
    [InlineData("{\"inputs\":{\"one\":\"\"}}")]
    [InlineData("{\"outputs\":{\"bad key\":\"parent\"}}")]
    [InlineData("{\"inputs\":{\"one\":\"same\",\"two\":\"same\"}}")]
    [InlineData("{\"outputs\":{\"one\":\"same\",\"two\":\"same\"}}")]
    [InlineData("{\"keep_locals\":true}")]
    public async Task MalformedPpmsScopeMappingsRejectWithLocationBeforeDeviceAdmission(string p)
    {
        var b = new Bench(); var node = Node("subtree", JsonNode.Parse(p)!.AsObject()); node["source_line"] = 19;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings));
        Assert.Contains("source line 19", error.Message); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("inputs_null")]
    [InlineData("outputs_array")]
    [InlineData("inputs_number")]
    [InlineData("outputs_null_value")]
    [InlineData("outputs_duplicate_target")]
    [InlineData("missing_inputs")]
    [InlineData("missing_children")]
    [InlineData("unknown")]
    public void NativeScopeRejectsMalformedMapContracts(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var text = codec.Serialize(new("Native scope", new SubtreeStep(new("scope"), [])));
        var node = JsonNode.Parse(text)!.AsObject(); var root = node["root"]!.AsObject();
        switch (damage)
        {
            case "inputs_null": root["inputs"] = null; break;
            case "outputs_array": root["outputs"] = new JsonArray(); break;
            case "inputs_number": root["inputs"] = new JsonObject { ["source"] = 1 }; break;
            case "outputs_null_value": root["outputs"] = new JsonObject { ["source"] = null }; break;
            case "outputs_duplicate_target": root["outputs"] = new JsonObject { ["one"] = "same", ["two"] = "same" }; break;
            case "missing_inputs": root.Remove("inputs"); break;
            case "missing_children": root.Remove("children"); break;
            case "unknown": root["export_all"] = true; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(node.ToJsonString()));
    }

    [Theory]
    [InlineData("local_leak")]
    [InlineData("missing_input")]
    [InlineData("missing_output")]
    [InlineData("unit_collision")]
    [InlineData("partial_output")]
    public async Task ScopeDependenciesAndOutputUnitsPreflightEveryChildBeforeIo(string error)
    {
        var b = new Bench(); SubtreeStep scope = error switch
        {
            "missing_input" => new(new("scope"), [], [new(new("missing"), new("alias"))]),
            "missing_output" => new(new("scope"), [], outputs: [new(new("missing"), new("selected"))]),
            "unit_collision" => new(new("scope"), [Local("define", "current", new(1, PhysicalUnit.Ampere))], outputs: [new(new("current"), new("value"))]),
            "partial_output" => new(new("scope"), [new IfElseStep(new("branch"), new(Flag, ComparisonOperator.Equal, new ConstantValue(Number(1))), Local("define", "local.only", Number(2)))], outputs: [new(new("local.only"), new("selected"))]),
            _ => new(new("scope"), [Local("define", "local.only", Number(2))]),
        };
        var steps = new List<ExperimentStep> { Local("seed", "value", Number(1)), scope };
        if (error == "local_leak") steps.Add(Equal("use", "local.only", Number(2)));
        steps.Add(b.ReadField("after"));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Powered(Seed(new SequenceStep(new("flow"), steps)))));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public void ForceSuccessAroundEarlyFailedScopeCannotGuaranteeNewExport()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"), [Check("failed", false), Local("define", "local.only", Number(2))], outputs: [new(new("local.only"), new("selected"))]);
        var plan = new ExperimentPlan("Availability", Seed(new SequenceStep(new("root"), [new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, scope), Equal("use", "selected", Number(2))])));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan));
    }

    [Fact]
    public async Task ScopeDoesNotHideExecutionBudgetOrInvalidSkippedChildren()
    {
        var b = new Bench(); var huge = new RetryStep(new("retry"), 1000000, TimeSpan.Zero, new RepeatStep(new("repeat"), 1000000, new DelayStep(new("wait"), TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Runs.PreviewPlan(new("Budget", new SubtreeStep(new("scope"), [huge]))));
        var child = new ConditionStep(new("bad"), new(new("missing"), ComparisonOperator.Equal, new ConstantValue(Number(0))));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Hidden", new FallbackStep(new("choose"), [new DelayStep(new("ok"), TimeSpan.Zero), new SubtreeStep(new("scope"), [child])]))));
        Assert.Equal(2, b.Runs.PreviewPlan(new("Cost", new SubtreeStep(new("scope"), [b.ReadField("value")]))).MaximumStepExecutions);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task ActualNestedPpmsScopeFixtureExportsSelectedValuesAndPreservesScanArchiveCsvReplay()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"subtree-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-subtree-import.json"), b.Bindings, Token);
                var plan = new ExperimentPlan(document.Plan.Name, new SequenceStep(new("acceptance"), [document.Plan.Root,
                    Equal("exported_number", "iteration.copy", Number(2)), Equal("exported_field", "latest.field", new(0, PhysicalUnit.Tesla)), b.ReadField("after")]), metadata: document.Plan.Metadata);
                var native = b.Workspace.Inspect(plan); var file = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(file, native.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token); var data = b.Runs.LatestDataSet!; id = data.Id;
                Assert.Equal(5, data.Rows.Count); Assert.All(data.Rows, data.Schema.ValidateRow);
                Assert.Equal(new[] { 1.0, 1, 2, 2 }, data.Rows.Take(4).Select(r => r.Values[new("ppms.root.axis")].Value));
                Assert.False(data.Rows[^1].Values.ContainsKey(new("ppms.root.axis"))); Assert.All(data.Rows, row => Assert.Single(row.Quality));
                Assert.Equal(8, data.Plan!.Metadata["ppms.root.0.0.source_line"].Value);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await reopened.LoadAsync(id, Token);
            Assert.Equal(5, loaded.DataSet.Rows.Count); Assert.Equal(MeasurementRunState.Completed, loaded.DataSet.State);
            var csv = Path.Combine(folder, "scope.csv"); await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("subtree", text); Assert.Contains("latest.field", text); Assert.Contains("ppms.root.axis", text); Assert.Contains("quality_json", text);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(loaded.DataSet.Plan!, Token); Assert.Equal(5, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

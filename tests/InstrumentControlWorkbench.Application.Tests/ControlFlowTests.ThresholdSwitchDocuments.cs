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
    [InlineData("switch")]
    [InlineData("select_case")]
    public async Task ThresholdSwitchNestedIteratorImportSavesRunsRestartsAndReexecutes(string type)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"icw-switch-{Guid.NewGuid():N}"); Directory.CreateDirectory(folder);
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var choice = Node(type, new JsonObject { ["variable"] = "value", ["case_1"] = 10, ["case_2"] = 0, ["case_3"] = 5 },
                    FieldMeasurement(), FieldMeasurement(), FieldMeasurement(), FieldMeasurement()); choice["source_line"] = 42;
                var root = Node("for_each", new JsonObject { ["points"] = new JsonArray(-1, 0, 4, 5, 10) }, Node("sequence", null, choice));
                var document = b.Workspace.Import(root.ToJsonString(), b.Bindings); Assert.Contains("threshold_switch", document.Text);
                var file = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(file, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token); id = b.Runs.LatestDataSet!.Id;
                Assert.Equal(new[] { 3, 1, 1, 2, 2 }.Select(i => $"ppms.root.0.0.{i}.value"),
                    b.Runs.LatestDataSet.Rows.Select(r => Assert.Single(r.Values, v => v.Key.Value.EndsWith(".value", StringComparison.Ordinal) && v.Value.Unit == PhysicalUnit.Tesla).Key.Value));
                Assert.Equal(42, document.Plan.Metadata["ppms.root.0.0.source_line"].Value);
            }
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(id, Token);
            Assert.Equal(5, saved.DataSet.Rows.Count); Assert.NotNull(saved.DataSet.Blackboard); Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State);
            var b2 = new Bench(); var native = b2.Workspace.Inspect(saved.DataSet.Plan!); await b2.Runs.StartPlanAsync(b2.Workspace.Validate(native.Text).Plan, Token);
            Assert.Equal(saved.DataSet.Rows.Select(r => r.Values.Keys.OrderBy(k => k.Value).ToArray()), b2.Runs.LatestDataSet!.Rows.Select(r => r.Values.Keys.OrderBy(k => k.Value).ToArray()));
            var csv = Path.Combine(folder, "data.csv"); await fresh.ExportCsvAsync(id, csv, Token); Assert.Contains("threshold_switch", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("{}", 2)]
    [InlineData("{\"case_2\":0}", 2)]
    [InlineData("{\"case_1\":10,\"case_3\":-1}", 2)]
    [InlineData("{\"case_4\":-1}", 2)]
    [InlineData("{\"case_1\":0,\"case_2\":0}", 1)]
    [InlineData("{\"case_1\":100,\"case_2\":200,\"case_3\":300}", 2)]
    public async Task ThresholdSwitchPpmsConsumesOnlyTheContiguousPrefixAndDefaultsToLastChild(string cases, int selected)
    {
        var b = new Bench(); var seed = Node("delay", new JsonObject { ["delay_s"] = 0 }); seed["_onSuccess"] = new JsonObject { ["variable"] = "count", ["action"] = "set", ["value"] = 0 };
        var p = JsonNode.Parse(cases)!.AsObject(); p["variable"] = "count";
        var root = Node("sequence", null, seed, Node("switch", p, FieldMeasurement(), FieldMeasurement(), FieldMeasurement()));
        var document = b.Workspace.Import(root.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(document.Plan, Token);
        Assert.True(Assert.Single(b.Runs.LatestDataSet!.Rows).Values.ContainsKey(new($"ppms.root.1.{selected}.value")));
    }

    [Theory]
    [InlineData("{\"variable\":\"field\",\"case_0\":0}")]
    [InlineData("{\"variable\":\"field\",\"case_01\":0}")]
    [InlineData("{\"variable\":\"field\",\"case_10001\":0}")]
    [InlineData("{\"variable\":\"field\",\"case_1\":null}")]
    [InlineData("{\"variable\":\"field\",\"case_1\":\"0\"}")]
    [InlineData("{\"variable\":\"field\",\"case_3\":false}")]
    [InlineData("{\"variable\":\"unknown_axis\",\"case_1\":0}")]
    [InlineData("{}")]
    public async Task ThresholdSwitchMalformedImportReportsPathAndNeverAdmitsDevices(string parameters)
    {
        var b = new Bench(); var root = Node("switch", JsonNode.Parse(parameters)!.AsObject(), FieldMeasurement()); root["source_line"] = 7;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(root.ToJsonString(), b.Bindings)); Assert.Contains("ppms.root", error.Message); Assert.Contains("source line 7", error.Message);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task ThresholdSwitchEmptyPpmsDoesNotReadAnUnavailableSelector()
    {
        var b = new Bench(); var plan = b.Workspace.Import(Node("switch").ToJsonString(), b.Bindings).Plan;
        await b.Runs.StartPlanAsync(plan, Token); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Empty(b.Runs.LatestBlackboard!);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }

    [Fact]
    public async Task ThresholdSwitchPpmsFieldScanUsesPhysicalThresholdUnits()
    {
        var b = new Bench(); var plan = (await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-threshold-switch-import.json"), b.Bindings, Token)).Plan;
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(new[] { "ppms.root.0.2.value", "ppms.root.0.0.value", "ppms.root.0.1.value" },
            b.Runs.LatestDataSet!.Rows.Select(r => Assert.Single(r.Values, v => v.Key.Value.EndsWith(".value", StringComparison.Ordinal) && v.Value.Unit == PhysicalUnit.Tesla).Key.Value));
    }

    [Theory]
    [InlineData("unknown_field")]
    [InlineData("missing_thresholds")]
    [InlineData("too_many_thresholds")]
    [InlineData("threshold_unit")]
    [InlineData("null_child")]
    public void ThresholdSwitchNativeCodecRejectsMalformedDocuments(string invalid)
    {
        var codec = new JsonExperimentPlanDocument(); var plan = new ExperimentPlan("switch", new ThresholdSwitchStep(new("choice"), new("value"), [new DelayStep(new("child"), TimeSpan.Zero)], [Number(0)]));
        var json = JsonNode.Parse(codec.Serialize(plan))!; var root = json["root"]!;
        if (invalid == "unknown_field") root["extra"] = true;
        if (invalid == "missing_thresholds") root.AsObject().Remove("thresholds");
        if (invalid == "too_many_thresholds") root["thresholds"]!.AsArray().Add(root["thresholds"]![0]!.DeepClone());
        if (invalid == "threshold_unit") root["thresholds"]![0]!["unit"] = "Unspecified";
        if (invalid == "null_child") root["children"]![0] = null;
        Assert.Throws<FormatException>(() => codec.Parse(json.ToJsonString()));
    }
}

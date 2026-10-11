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
    [Fact]
    public async Task RealTypedNativeFixtureRunsWithoutDevicesAndKeepsOnlySelectedScopeResults()
    {
        var b = new Bench(); var document = await b.Workspace.LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "typed-blackboard-native.json"), Token);
        Assert.Empty(document.Preview.Devices); Assert.True(document.Preview.HasDynamicLoops);
        await b.Runs.StartPlanAsync(document.Plan, Token); var entries = b.Runs.LatestBlackboard!;
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.True(Assert.IsType<BlackboardBoolean>(entries[new("ready")].Value).Value);
        Assert.False(Assert.IsType<BlackboardBoolean>(entries[new("completed")].Value).Value);
        Assert.Equal("硅片A / Si", Assert.IsType<BlackboardText>(entries[new("sample")].Value).Value);
        Assert.False(entries.ContainsKey(new("local.ready"))); Assert.False(entries.ContainsKey(new("local.sample")));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BooleanIfElseJoinsTextTypesAndRejectsIncompatibleOrMissingBranchValues(bool enabled)
    {
        var b = new Bench();
        var branch = new IfElseStep(new("choose"), new(new("enabled"), ComparisonOperator.Equal, new BooleanValue(true)),
            TypedWrite("accepted", "label", new TextValue("accepted")), TypedWrite("rejected", "label", new TextValue("rejected")));
        var plan = new ExperimentPlan("Typed join", new SequenceStep(new("root"), [TypedWrite("seed", "enabled", new BooleanValue(enabled)), branch,
            TypedCheck("verify", "label", new TextValue(enabled ? "accepted" : "rejected")), b.ReadField("observed")]));
        await b.Runs.StartPlanAsync(b.Workspace.Validate(b.Workspace.Inspect(plan).Text).Plan, Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        var changed = new IfElseStep(branch.Id, branch.Condition, branch.Then, TypedWrite("wrong_type", "label", new BooleanValue(false)));
        var missing = new IfElseStep(branch.Id, branch.Condition, branch.Then);
        foreach (var bad in new[] { changed, missing })
            Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Invalid join", new SequenceStep(new("root"), [TypedWrite("seed", "enabled", new BooleanValue(enabled)), bad, TypedCheck("use", "label", new TextValue("accepted"))]))));
    }

    [Theory]
    [InlineData("{\"boolean\":null}")]
    [InlineData("{\"boolean\":1}")]
    [InlineData("{\"boolean\":\"true\"}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":true}")]
    [InlineData("{\"text\":[]}")]
    [InlineData("{\"text\":\"sample\",\"boolean\":true}")]
    [InlineData("{\"text\":\"sample\",\"unknown\":0}")]
    public async Task MalformedTypedLiteralsFailAtTheFormatBoundaryBeforeConnection(string malformed)
    {
        var b = new Bench(); var root = JsonNode.Parse(b.Workspace.Inspect(new("Literal", TypedWrite("seed", "label", new TextValue("valid")))).Text)!.AsObject();
        root["root"]!["value"] = JsonNode.Parse(malformed);
        Assert.Throws<FormatException>(() => b.Workspace.Validate(root.ToJsonString()));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("{\"text\":\"a\",\"text\":\"b\"}")]
    [InlineData("{\"boolean\":true,\"boolean\":false}")]
    public async Task DuplicateTypedLiteralKeysAreFormatErrorsRatherThanDomExceptions(string literal)
    {
        var b = new Bench(); var text = new JsonExperimentPlanDocument().Serialize(new("Duplicate", TypedWrite("seed", "value", new TextValue("placeholder"))));
        var document = JsonNode.Parse(text)!;
        document["root"]!["value"] = new TextValue("placeholder").Value;
        var damaged = document.ToJsonString().Replace("\"placeholder\"", literal, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => b.Workspace.Validate(damaged));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task SharedTextValuesCannotBypassNativeAllocationBudgetOrLiteralLengthLimit()
    {
        var b = new Bench(); var literal = new TextValue(new string('x', BlackboardText.MaximumLength));
        var plan = new ExperimentPlan("Text budget", new SequenceStep(new("root"), Enumerable.Range(0, 65).Select(i => TypedWrite("write" + i, "label" + i, literal))));
        Assert.Throws<FormatException>(() => b.Workspace.Inspect(plan));
        var json = JsonNode.Parse(new JsonExperimentPlanDocument().Serialize(new("Limit", TypedWrite("seed", "label", new TextValue("valid")))))!;
        json["root"]!["value"]!["text"] = new string('x', BlackboardText.MaximumLength + 1);
        Assert.Throws<FormatException>(() => b.Workspace.Validate(json.ToJsonString())); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task NativeFileArchiveCsvAndFreshReplayPreserveTypedPlanAndRealNumericRows()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var text = "晶圆A,\"Si\"\n🧪";
                var scope = new SubtreeStep(new("scope"), [TypedWrite("verdict", "ready", new BooleanValue(true)), b.ReadField("inside")], outputs: [new(new("ready"), new("completed"))]);
                var plan = new ExperimentPlan("Typed archive", new SequenceStep(new("root"), [TypedWrite("sample", "sample", new TextValue(text)),
                    TypedWrite("not_ready", "ready", new BooleanValue(false)), scope, TypedCheck("kept", "ready", new BooleanValue(false)),
                    TypedCheck("exported", "completed", new BooleanValue(true)), TypedCheck("label", "sample", new TextValue(text)), b.ReadField("after")]));
                var document = b.Workspace.Inspect(plan); var file = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(file, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token); id = b.Runs.LatestDataSet!.Id;
                Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count); Assert.Equal(BlackboardWriteKind.SubtreeOutput, b.Runs.LatestBlackboard![new("completed")].Kind);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await reopened.LoadAsync(id, Token);
            Assert.Equal(MeasurementRunState.Completed, loaded.DataSet.State); Assert.Equal(2, loaded.DataSet.Rows.Count);
            var csv = Path.Combine(folder, "typed.csv"); await reopened.ExportCsvAsync(id, csv, Token); var content = await File.ReadAllTextAsync(csv);
            Assert.Contains("plan_json", content); Assert.Contains("boolean", content); Assert.Contains("晶圆A", content); Assert.Contains("quality_json", content);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(loaded.DataSet.Plan!, Token); Assert.Equal(2, fresh.Runs.LatestDataSet!.Rows.Count);
            Assert.True(Assert.IsType<BlackboardBoolean>(fresh.Runs.LatestBlackboard![new("completed")].Value).Value);
            Assert.Equal(new StepId("scope"), fresh.Runs.LatestBlackboard[new("completed")].Source);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

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
    [InlineData("timeout", "{}", 0)]
    [InlineData("timeout", "{}", 2)]
    [InlineData("timeout", "{\"timeout_s\":-1}", 1)]
    [InlineData("timeout", "{\"timeout_s\":4294967.295}", 1)]
    [InlineData("timeout", "{\"timeout_s\":\"1\"}", 1)]
    [InlineData("timeout", "{\"timeout_s\":null}", 1)]
    [InlineData("with_timeout", "{\"timeout_s\":true}", 1)]
    [InlineData("with_timeout", "{\"ignore_errors\":true}", 1)]
    public async Task MalformedPpmsTimeoutRejectsBeforeIoWithSourceLocation(string type, string p, int children)
    {
        var b = new Bench(); var node = Node(type, JsonNode.Parse(p)!.AsObject(), Enumerable.Range(0, children).Select(i => Node("delay", new() { ["delay_s"] = 0 })).ToArray());
        node["source_line"] = 15;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings));
        Assert.Contains("source line 15", error.Message); Assert.Contains("ppms.root", error.Message);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("with_timeout")]
    public async Task PpmsTimeoutUsesSixtySecondDefaultAndNativeExecutableBody(string type)
    {
        var b = new Bench(); var child = Node("measurement"); child["name"] = "measure_field";
        var document = b.Workspace.Import(Node(type, null, child).ToJsonString(), b.Bindings);
        var step = Assert.IsType<TimeoutStep>(document.Plan.Root); Assert.Equal(TimeSpan.FromSeconds(60), step.Timeout);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(document.Text).Plan, Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("string")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("missing_duration")]
    [InlineData("missing_body")]
    public void NativeTimeoutCodecRejectsMalformedFieldsWithoutDowngrading(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var plan = new ExperimentPlan("Native", new TimeoutStep(new("timeout"), TimeSpan.FromSeconds(1), new DelayStep(new("body"), TimeSpan.Zero)));
        var node = JsonNode.Parse(codec.Serialize(plan))!.AsObject(); var root = node["root"]!.AsObject();
        switch (damage)
        {
            case "negative": root["timeoutSeconds"] = -1; break;
            case "overflow": root["timeoutSeconds"] = 4294967.295; break;
            case "string": root["timeoutSeconds"] = "1"; break;
            case "null": root["timeoutSeconds"] = null; break;
            case "unknown": root["ignoreErrors"] = true; break;
            case "missing_duration": root.Remove("timeoutSeconds"); break;
            case "missing_body": root.Remove("body"); break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(node.ToJsonString()));
    }

    [Fact]
    public async Task TimeoutStillPreflightsUnreachableChildAndCountsWorstCaseExecutions()
    {
        var b = new Bench();
        var bad = new ConditionStep(new("invalid"), new(new("unknown"), ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless))));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Hidden", new TimeoutStep(new("zero"), TimeSpan.Zero, bad))));
        var huge = new RetryStep(new("retry"), 1000000, TimeSpan.Zero, new RepeatStep(new("repeat"), 1000000, new DelayStep(new("body"), TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Runs.PreviewPlan(new("Budget", new TimeoutStep(new("short"), TimeSpan.FromMilliseconds(1), huge))));
        Assert.Equal(2, b.Runs.PreviewPlan(new("Cost", new TimeoutStep(new("cost"), TimeSpan.FromSeconds(1), b.ReadField("value")))).MaximumStepExecutions);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task ForceSuccessCannotGuaranteeVariablesPastEarlyLogicalFailureInsideTimeout()
    {
        var b = new Bench(); var variable = new ParameterId("late");
        var body = new SequenceStep(new("body"), [Check("fail", false), new AssignValueStep(new("assign"), variable, new ConstantValue(new(1, PhysicalUnit.Dimensionless)))]);
        var scope = new TimeoutStep(new("timeout"), TimeSpan.FromSeconds(1), body);
        var plan = new ExperimentPlan("Availability", Seed(new SequenceStep(new("root"), [new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, scope),
            new ConditionStep(new("use"), new(variable, ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))))])));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task ActualPpmsTimeoutFixtureSavesArchivesExportsAndReexecutesOnFreshSession()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"timeout-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-timeout-import.json"), b.Bindings, Token);
                Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
                var saved = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(saved, document.Text, Token);
                var plan = (await b.Workspace.LoadAsync(saved, Token)).Plan; Assert.IsType<TimeoutStep>(((SequenceStep)plan.Root).Children[0]);
                await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; id = data.Id;
                Assert.Equal(3, data.Rows.Count); Assert.All(data.Rows, data.Schema.ValidateRow);
                Assert.All(data.Rows, row => Assert.Single(row.Quality)); Assert.Single(data.Instruments);
                Assert.Equal(4, data.Plan!.Metadata["ppms.root.0.0.2.source_line"].Value);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await reopened.LoadAsync(id, Token);
            Assert.True(loaded.IsFinalized); Assert.Equal(MeasurementRunState.Completed, loaded.DataSet.State); Assert.Equal(3, loaded.DataSet.Rows.Count);
            var csv = Path.Combine(folder, "timeout.csv"); await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("timeoutSeconds", text); Assert.Contains("[Tesla]", text); Assert.Contains("quality_json", text);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(loaded.DataSet.Plan!, Token); Assert.Equal(3, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ExpiredTimeoutArchivesPartialRowsAndTypedPlanInsteadOfReportingCompletion()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"expired-timeout-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var body = new SequenceStep(new("body"), [b.ReadField("before"), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), b.ReadField("after")]);
                var plan = b.Powered(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(200), body));
                await Assert.ThrowsAsync<PlanStepTimeoutException>(() => b.Runs.StartPlanAsync(plan, Token)); id = b.Runs.LatestDataSet!.Id;
                Assert.Single(b.Runs.LatestDataSet.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await reopened.LoadAsync(id, Token);
            Assert.True(loaded.IsFinalized); Assert.Equal(MeasurementRunState.Faulted, loaded.DataSet.State); Assert.Single(loaded.DataSet.Rows);
            var timeout = Assert.IsType<TimeoutStep>(((SequenceStep)loaded.DataSet.Plan!.Root).Children[1]); Assert.Equal(TimeSpan.FromMilliseconds(200), timeout.Timeout);
            var csv = Path.Combine(folder, "partial.csv"); await reopened.ExportCsvAsync(id, csv, Token);
            Assert.Contains("Faulted", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

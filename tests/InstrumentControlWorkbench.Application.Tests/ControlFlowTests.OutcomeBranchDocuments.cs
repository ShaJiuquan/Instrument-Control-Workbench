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
    [InlineData("early_failure")]
    [InlineData("missing_else")]
    [InlineData("one_branch")]
    [InlineData("unit_collision")]
    [InlineData("hidden_invalid")]
    public async Task OutcomeBranchRejectsUnavailableOrIncompatibleVariablesBeforeIo(string kind)
    {
        var b = new Bench(); var assign = Local("assign", "selected", Number(1));
        ExperimentStep body = kind switch
        {
            "early_failure" => new BranchOnOutcomeStep(new("branch"), new SequenceStep(new("condition"), [Check("failed", false), assign]), b.ReadField("then"), Equal("unsafe", "selected", Number(1))),
            "missing_else" => new SequenceStep(new("flow"), [new BranchOnOutcomeStep(new("branch"), Check("predicate", true), assign), Equal("unsafe", "selected", Number(1))]),
            "one_branch" => new SequenceStep(new("flow"), [new BranchOnOutcomeStep(new("branch"), Check("predicate", true), assign, b.ReadField("else")), Equal("unsafe", "selected", Number(1))]),
            "unit_collision" => new SequenceStep(new("flow"), [new BranchOnOutcomeStep(new("branch"), Check("predicate", true), assign, Local("other", "selected", new(1, PhysicalUnit.Volt))), Equal("unsafe", "selected", Number(1))]),
            _ => new BranchOnOutcomeStep(new("branch"), b.ReadField("condition"), b.ReadField("then"), Equal("unsafe", "missing", Number(1))),
        };
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Powered(Seed(body))));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothBranchesCanGuaranteeSameUnitValueDownstream(bool success)
    {
        var b = new Bench(); var branch = new BranchOnOutcomeStep(new("branch"), Check("predicate", success), Local("then", "chosen", Number(1)), Local("else", "chosen", Number(2)));
        await b.Runs.StartPlanAsync(new("Availability", Seed(new SequenceStep(new("flow"), [branch, Equal("use", "chosen", Number(success ? 1 : 2)), b.ReadField("after")]))), Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Fact]
    public async Task BranchBudgetIncludesConditionAndLargestBranchAndCannotHideHugeElse()
    {
        var b = new Bench(); var branch = new BranchOnOutcomeStep(new("branch"), new DelayStep(new("condition"), TimeSpan.Zero), b.ReadField("then"), new RepeatStep(new("else"), 3, b.ReadField("else_read")));
        Assert.Equal(6, b.Runs.PreviewPlan(new("Cost", branch)).MaximumStepExecutions);
        var huge = new RepeatStep(new("huge"), 1000000, new RepeatStep(new("nested"), 1000000, b.ReadField("value")));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Runs.PreviewPlan(new("Huge", new BranchOnOutcomeStep(new("branch"), branch.Condition, branch.Then, huge))));
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("if_then_else")]
    [InlineData("conditional")]
    [InlineData("branch")]
    public async Task PpmsOutcomeBranchAliasesAndThenElseWrappersPreserveLocation(string type)
    {
        var b = new Bench(); var read = Node("measurement"); read["name"] = "measure_field";
        var node = Node(type, null, Node("force_failure", null, read), Node("then", null, read), Node("else", null, read)); node["source_line"] = 17;
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings); var branch = Assert.IsType<BranchOnOutcomeStep>(document.Plan.Root);
        Assert.IsType<SequenceStep>(branch.Then); Assert.IsType<SequenceStep>(branch.Else); Assert.Equal(17, document.Plan.Metadata["ppms.root.source_line"].Value);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(document.Text).Plan, Token); Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
    }

    [Theory]
    [InlineData(0, "{}")]
    [InlineData(1, "{}")]
    [InlineData(4, "{}")]
    [InlineData(2, "{\"unknown\":true}")]
    [InlineData(2, "{\"variable\":\"value\",\"op\":\"gt\",\"value\":1}")]
    public async Task UnsupportedOrMalformedPpmsBranchRejectsBeforeAdmission(int count, string p)
    {
        var b = new Bench(); var node = Node("if_then_else", JsonNode.Parse(p)!.AsObject(), Enumerable.Range(0, count).Select(_ => Node("delay", new() { ["delay_s"] = 0 })).ToArray()); node["source_line"] = 29;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings)); Assert.Contains("source line 29", error.Message);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("missing_condition")]
    [InlineData("missing_then")]
    [InlineData("null_condition")]
    [InlineData("null_then")]
    [InlineData("null_else")]
    [InlineData("unknown")]
    public void NativeOutcomeBranchRejectsMalformedChildren(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var node = JsonNode.Parse(codec.Serialize(new("Branch", new BranchOnOutcomeStep(new("branch"), new DelayStep(new("condition"), TimeSpan.Zero), new DelayStep(new("then"), TimeSpan.Zero)))))!.AsObject();
        var root = node["root"]!.AsObject();
        if (damage.StartsWith("missing_", StringComparison.Ordinal)) root.Remove(damage[8..]);
        else if (damage.StartsWith("null_", StringComparison.Ordinal)) root[damage[5..]] = null;
        else root["ignore_errors"] = true;
        Assert.Throws<FormatException>(() => codec.Parse(node.ToJsonString()));
    }

    [Fact]
    public async Task PpmsNodeBranchFixtureRoundTripsArchiveCsvAndFreshExecution()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"branch-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-outcome-branch-import.json"), b.Bindings, Token);
                Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
                var file = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(file, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token);
                var data = b.Runs.LatestDataSet!; id = data.Id; Assert.Equal(5, data.Rows.Count); Assert.All(data.Rows, data.Schema.ValidateRow);
                Assert.True(data.Rows[1].Values.ContainsKey(new("ppms.root.0.1.0.value")));
                Assert.True(data.Rows[3].Values.ContainsKey(new("ppms.root.1.2.0.value")));
                Assert.Equal(8, data.Plan!.Metadata["ppms.root.1.source_line"].Value);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(id, Token);
            Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State); Assert.Equal(5, saved.DataSet.Rows.Count);
            var csv = Path.Combine(folder, "branch.csv"); await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("branch_outcome", text); Assert.Contains("quality_json", text);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Equal(5, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

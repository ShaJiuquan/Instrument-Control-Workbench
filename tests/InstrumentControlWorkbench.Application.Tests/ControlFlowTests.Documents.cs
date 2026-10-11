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
    [InlineData("force_success")]
    [InlineData("force_failure")]
    [InlineData("inverter")]
    public async Task PpmsDecoratorImportRunsAndPreservesSourceLine(string type)
    {
        var b = new Bench(); var read = Node("measurement"); read["name"] = "measure_field";
        var root = Node(type, null, read); root["source_line"] = 12;
        var document = b.Workspace.Import(root.ToJsonString(), b.Bindings);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected); Assert.Equal(12, document.Plan.Metadata["ppms.root.source_line"].Value);
        Assert.Contains("result_decorator", document.Text); Assert.Empty(document.Plan.Preparations);
        if (type == "force_success") await b.Runs.StartPlanAsync(document.Plan, Token);
        else await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(document.Plan, Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows);
    }

    [Theory]
    [InlineData("retry", "{\"max_attempts\":0}", 1)]
    [InlineData("retry", "{\"max_attempts\":1.5}", 1)]
    [InlineData("retry", "{\"delay_ms\":-1}", 1)]
    [InlineData("retry", "{\"delay_ms\":0.5}", 1)]
    [InlineData("retry", "{\"delay_ms\":4294967295}", 1)]
    [InlineData("retry", "{}", 0)]
    [InlineData("retry", "{}", 2)]
    [InlineData("fallback", "{}", 0)]
    [InlineData("force_success", "{\"ignore_errors\":true}", 1)]
    [InlineData("force_failure", "{}", 2)]
    [InlineData("inverter", "{}", 0)]
    public async Task MalformedPpmsControlsRejectBeforeDeviceIoAndIdentifySourceLine(string type, string p, int children)
    {
        var b = new Bench(); var node = Node(type, JsonNode.Parse(p)!.AsObject(), Enumerable.Range(0, children).Select(i => Node("delay", new() { ["delay_s"] = 0 })).ToArray()); node["source_line"] = 27;
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings)); Assert.Contains("source line 27", error.Message);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("subtree")]
    [InlineData("branch_outcome")]
    [InlineData("while")]
    [InlineData("outcome_mutation")]
    public async Task NativeControlsSaveArchiveReopenAndReexecuteGroupedQualityWithNestedScopeColumns(string scope)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"control-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var group = new MeasurementGroupStep(new("point"), [new("Electrical", b.ReadSource("electrical")), new("Field", b.ReadField("field"))]);
                var retry = new RetryStep(new("retry"), 3, TimeSpan.FromMilliseconds(1), new SequenceStep(new("attempt"), [group,
                    new ConditionStep(new("ready"), new(new("retry.attempt"), ComparisonOperator.Equal, new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]));
                var fallback = new FallbackStep(new("choose"), [new ResultDecoratorStep(new("first"), ResultEffect.ForceFailure, b.ReadSource("rejected")), retry]);
                var plan = b.Powered(new SequenceStep(new("flow"), [fallback, b.ReadField("after")]));
                if (scope != "plain") plan = new(plan.Name, scope switch
                {
                    "subtree" => new SubtreeStep(new("scoped.plan"), [plan.Root]),
                    "outcome_mutation" => new OutcomeMutationStep(new("hooked.plan"), plan.Root, Mutate("done", "count", VariableMutation.Set, 1)),
                    "while" => new SequenceStep(new("looped.plan"), [Local("count_seed", "count", Number(0)), new WhileStep(new("loop"), Below("count", 1), new SequenceStep(new("loop_body"), [plan.Root, Mutate("advance", "count", VariableMutation.Add, 1)]))]),
                    _ => new BranchOnOutcomeStep(new("conditional.plan"), new DelayStep(new("condition"), TimeSpan.Zero), plan.Root),
                }, plan.Preparations, plan.Metadata, plan.Routing);
                var document = b.Workspace.Inspect(plan); var file = Path.Combine(folder, "saved.plan.json"); await b.Workspace.SaveAsync(file, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(file, Token)).Plan, Token);
                var data = b.Runs.LatestDataSet!; id = data.Id; Assert.Equal(8, data.Rows.Count); Assert.All(data.Rows, data.Schema.ValidateRow);
                Assert.Equal(1, data.Rows[0].Values[fallback.BranchColumn].Value);
                Assert.All(data.Rows.Skip(1).Take(6), r => Assert.Equal(2, r.Values[fallback.BranchColumn].Value));
                Assert.Equal(new[] { 1.0, 1, 1, 2, 2, 2 }, data.Rows.Skip(1).Take(6).Select(r => r.Values[retry.AttemptColumn].Value));
                Assert.False(data.Rows[^1].Values.ContainsKey(fallback.BranchColumn)); Assert.False(data.Rows[^1].Values.ContainsKey(retry.AttemptColumn));
                Assert.Contains(data.Rows, r => r.Quality.Count > 0); Assert.Equal(2, data.Rows.Count(r => r.MeasurementGroup is not null));
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(id, Token); Assert.Equal(8, saved.DataSet.Rows.Count);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan!, Token); Assert.Equal(8, fresh.Runs.LatestDataSet!.Rows.Count);
            var fileCsv = Path.Combine(folder, "flow.csv"); await reopened.ExportCsvAsync(id, fileCsv, Token); var csv = await File.ReadAllTextAsync(fileCsv);
            Assert.Contains("choose.branch", csv); Assert.Contains("retry.attempt", csv); Assert.Contains("ForceFailure", csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ActualPpmsControlFixtureRunsFiveRowsAndReopensAsANativeExecutablePlan()
    {
        var b = new Bench();
        var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-fallback-retry-import.json"), b.Bindings, Token);
        Assert.Empty(document.Plan.Preparations); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
        var native = b.Workspace.Validate(document.Text); await b.Runs.StartPlanAsync(native.Plan, Token);
        var data = b.Runs.LatestDataSet!; Assert.Equal(5, data.Rows.Count);
        Assert.Equal(new[] { 1.0, 2, 2, 3 }, data.Rows.Take(4).Select(r => r.Values[new("ppms.root.0.branch")].Value));
        Assert.Equal(new[] { 1.0, 2.0 }, data.Rows.Skip(1).Take(2).Select(r => r.Values[new("ppms.root.0.1.attempt")].Value));
        Assert.False(data.Rows[^1].Values.ContainsKey(new("ppms.root.0.branch"))); Assert.All(data.Rows, data.Schema.ValidateRow);
        Assert.Equal(3, data.Plan!.Metadata["ppms.root.0.1.source_line"].Value);
        Assert.Equal(MeasurementRunState.Completed, data.State); Assert.Single(data.Instruments);
        var fresh = new Bench(); await fresh.Runs.StartPlanAsync(native.Plan, Token); Assert.Equal(5, fresh.Runs.LatestDataSet!.Rows.Count);
    }

    [Theory]
    [InlineData("decorator")]
    [InlineData("fallback")]
    [InlineData("branch")]
    public async Task ValuesNotGuaranteedAfterControlCannotBeConsumedAsStaleMeasurements(string kind)
    {
        var b = new Bench(); var x = new ParameterId("x"); var assign = new AssignValueStep(new("define"), x, new ConstantValue(new(0, PhysicalUnit.Dimensionless)));
        ExperimentStep body = kind switch
        {
            "decorator" => new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, new SequenceStep(new("body"), [Check("early", false), assign])),
            "fallback" => new FallbackStep(new("choose"), [assign, new DelayStep(new("alternative"), TimeSpan.Zero)]),
            _ => new IfElseStep(new("branch"), new(Flag, ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))), assign),
        };
        var plan = new ExperimentPlan("unsafe variable", Seed(new SequenceStep(new("flow"), [body, new ConditionStep(new("use"), new(x, ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless))))])));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ForceSuccessAroundAnAcquisitionCannotHideErrorsAndRetainsItsGuaranteedVariable()
    {
        var b = new Bench();
        var plan = new ExperimentPlan("guaranteed variable", new SequenceStep(new("root"),
            [new ResultDecoratorStep(new("force"), ResultEffect.ForceSuccess, b.ReadField("field")),
             new ConditionStep(new("use"), new(new("field.value"), ComparisonOperator.GreaterOrEqual, new ConstantValue(new(0, PhysicalUnit.Tesla))))]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task RetryExecutionBudgetAndHiddenInvalidBranchesRejectBeforeAcquiringDevices()
    {
        var b = new Bench(); var huge = new ExperimentPlan("huge", new RetryStep(new("retry"), 1000000, TimeSpan.Zero, new RepeatStep(new("repeat"), 1000000, new DelayStep(new("body"), TimeSpan.Zero))));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Runs.PreviewPlan(huge));
        var hidden = new FallbackStep(new("choose"), [new DelayStep(new("ok"), TimeSpan.Zero), new ConditionStep(new("bad"), new(new("missing"), ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Volt))))]);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("hidden", hidden)));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("retry", "maxAttempts", "0")]
    [InlineData("result_decorator", "effect", "99")]
    [InlineData("condition", "operator", "Bogus")]
    public void NativeCodecRejectsUnknownOrInvalidControlWithoutDowngrading(string kind, string field, string value)
    {
        var codec = new JsonExperimentPlanDocument(); ExperimentStep step = kind switch
        {
            "retry" => new RetryStep(new("retry"), 2, TimeSpan.Zero, new DelayStep(new("body"), TimeSpan.Zero)),
            "condition" => Check("condition", true),
            _ => new ResultDecoratorStep(new("effect"), ResultEffect.Invert, new DelayStep(new("body"), TimeSpan.Zero)),
        };
        var node = JsonNode.Parse(codec.Serialize(new("bad", step)))!.AsObject(); node["root"]![field] = field == "maxAttempts" ? JsonValue.Create(0) : JsonValue.Create(value);
        if (field == "maxAttempts") Assert.Throws<ArgumentOutOfRangeException>(() => codec.Parse(node.ToJsonString()));
        else Assert.Throws<FormatException>(() => codec.Parse(node.ToJsonString()));
    }
}

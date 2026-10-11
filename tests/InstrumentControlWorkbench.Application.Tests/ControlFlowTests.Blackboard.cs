using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static AssignValueStep TypedWrite(string id, string variable, PlanValue value) => new(new(id), new(variable), value);
    private static ConditionStep TypedCheck(string id, string variable, PlanValue value, ComparisonOperator op = ComparisonOperator.Equal)
        => new(new(id), new(new(variable), op, value));

    [Theory]
    [InlineData("", "", true)]
    [InlineData("sample", "Sample", false)]
    [InlineData("硅片🧪", "硅片🧪", true)]
    [InlineData("é", "é", false)]
    public async Task TextConditionsUseExactOrdinalValuesAndPreserveFailure(string left, string right, bool equal)
    {
        var b = new Bench();
        var plan = new ExperimentPlan("Text", new SequenceStep(new("root"), [TypedWrite("seed", "sample", new TextValue(left)),
            new BranchOnOutcomeStep(new("choice"), TypedCheck("test", "sample", new TextValue(right)), b.ReadField("equal"), b.ReadField("different"))]));
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(equal ? "equal" : "different", b.Runs.LatestDataSet!.Rows.Single().Quality.Single().Step.Value);
        Assert.Equal(left, Assert.IsType<BlackboardText>(b.Runs.LatestBlackboard![new("sample")].Value).Value);
    }

    [Fact]
    public async Task BooleanWhileReevaluatesTypedWritesAndDoesNotCreateNonnumericDataColumns()
    {
        var b = new Bench();
        var loop = new WhileStep(new("loop"), new(new("ready"), ComparisonOperator.Equal, new BooleanValue(true)),
            new SequenceStep(new("body"), [b.ReadField("inside"), TypedWrite("finished", "ready", new BooleanValue(false))]));
        var plan = new ExperimentPlan("Boolean loop", new SequenceStep(new("root"), [TypedWrite("seed", "ready", new BooleanValue(true)), loop,
            TypedWrite("copy", "copied", new VariableValue(new("ready"))), TypedCheck("false", "copied", new BooleanValue(true), ComparisonOperator.NotEqual), b.ReadField("after")]));
        var native = b.Workspace.Inspect(plan); Assert.True(native.Preview.HasDynamicLoops);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(native.Text).Plan, Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.DoesNotContain(b.Runs.LatestDataSet.Schema.Columns, c => c.Parameter.Id.Value is "ready" or "copied");
        Assert.Equal(1, b.Runs.LatestDataSet.Rows[0].Values[new("loop.iteration")].Value); Assert.False(b.Runs.LatestDataSet.Rows[1].Values.ContainsKey(new("loop.iteration")));
        Assert.False(Assert.IsType<BlackboardBoolean>(b.Runs.LatestBlackboard![new("copied")].Value).Value);
        Assert.Equal(new StepId("copy"), b.Runs.LatestBlackboard[new("copied")].Source);
    }

    [Fact]
    public async Task TypedSubtreeCanShadowParentTypeAndOnlyPublishSelectedOutputsWithProvenance()
    {
        var b = new Bench(); var scope = new SubtreeStep(new("scope"), [TypedCheck("mapped", "sample", new BooleanValue(true)),
            TypedWrite("local", "sample", new BooleanValue(false)), TypedWrite("label", "local.label", new TextValue("合格")), b.ReadField("inside")],
            [new(new("enabled"), new("sample"))], [new(new("sample"), new("selected")), new(new("local.label"), new("result.label"))]);
        var plan = new ExperimentPlan("Typed scope", new SequenceStep(new("root"), [TypedWrite("seed_sample", "sample", new TextValue("Si")),
            TypedWrite("seed_enabled", "enabled", new BooleanValue(true)), scope, TypedCheck("parent", "sample", new TextValue("Si")),
            TypedCheck("result", "selected", new BooleanValue(false)), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(b.Workspace.Validate(b.Workspace.Inspect(plan).Text).Plan, Token);
        var values = b.Runs.LatestBlackboard!; Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.False(values.ContainsKey(new("local.label")));
        Assert.Equal(new StepId("seed_sample"), values[new("sample")].Source);
        Assert.Equal(new StepId("scope"), values[new("selected")].Source); Assert.Equal(BlackboardWriteKind.SubtreeOutput, values[new("selected")].Kind);
        Assert.Equal("合格", Assert.IsType<BlackboardText>(values[new("result.label")].Value).Value);
        Assert.All(values.Values, entry => { Assert.True(entry.Elapsed >= TimeSpan.Zero); Assert.True(entry.UpdatedAt >= b.Runs.LatestDataSet.StartedAt); });
        Assert.Throws<NotSupportedException>(() => ((IDictionary<ParameterId, BlackboardEntry>)values).Clear());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("abort")]
    public async Task FailedTimedOutOrAbortedTypedScopeRestoresParentEntriesAndExportsNothing(string mode)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var child = new SequenceStep(new("local"), [TypedWrite("overwrite", "sample", new TextValue("local")), TypedWrite("private", "ready", new BooleanValue(false)), b.ReadField("observed"),
            mode == "failure" ? TypedCheck("rejected", "ready", new BooleanValue(true)) : new DelayStep(new("pending"), TimeSpan.FromSeconds(30))]);
        ExperimentStep scoped = new SubtreeStep(new("scope"), [child], outputs: [new(new("ready"), new("exported"))]);
        if (mode == "timeout") scoped = new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(200), scoped);
        var plan = b.Powered(new SequenceStep(new("body"), [TypedWrite("seed", "sample", new TextValue("parent")), scoped]));
        var task = b.Runs.StartPlanAsync(plan, Token);
        if (mode == "abort") { await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Abort(); }
        var error = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        if (mode == "abort") { Assert.Null(error); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); }
        else { Assert.NotNull(error); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State); }
        Assert.Equal("parent", Assert.IsType<BlackboardText>(b.Runs.LatestBlackboard![new("sample")].Value).Value);
        Assert.Equal(new StepId("seed"), b.Runs.LatestBlackboard[new("sample")].Source);
        Assert.False(b.Runs.LatestBlackboard.ContainsKey(new("ready"))); Assert.False(b.Runs.LatestBlackboard.ContainsKey(new("exported")));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData(false, ComparisonOperator.Less)]
    [InlineData(false, ComparisonOperator.LessOrEqual)]
    [InlineData(false, ComparisonOperator.Greater)]
    [InlineData(false, ComparisonOperator.GreaterOrEqual)]
    [InlineData(true, ComparisonOperator.Less)]
    [InlineData(true, ComparisonOperator.LessOrEqual)]
    [InlineData(true, ComparisonOperator.Greater)]
    [InlineData(true, ComparisonOperator.GreaterOrEqual)]
    public async Task NonnumericOrderingIsRejectedBeforeSourceConnection(bool text, ComparisonOperator op)
    {
        var b = new Bench(); PlanValue literal = text ? new TextValue("sample") : new BooleanValue(true);
        var plan = b.Powered(new SequenceStep(new("body"), [TypedWrite("seed", "value", literal), TypedCheck("invalid", "value", literal, op)]));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("write")]
    [InlineData("qualify")]
    [InlineData("tolerance")]
    [InlineData("change_type")]
    [InlineData("mutation")]
    [InlineData("multiply")]
    [InlineData("output_type")]
    public async Task BooleanValuesCannotSilentlyBecomePhysicalOperandsOrChangeExistingTypes(string damage)
    {
        var b = new Bench(); ExperimentStep invalid = damage switch
        {
            "number" => TypedCheck("bad", "value", new ConstantValue(Number(1))),
            "write" => new SetParameterStep(new("bad"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new VariableValue(new("value"))),
            "qualify" => TypedWrite("bad", "copy", new QuantityFromNumberValue(new("value"), PhysicalUnit.Volt)),
            "tolerance" => new ConditionStep(new("bad"), new(new("value"), ComparisonOperator.Equal, new BooleanValue(true), Number(1e-9))),
            "change_type" => TypedWrite("bad", "value", new TextValue("true")),
            "mutation" => new MutateValueStep(new("bad"), new("value"), VariableMutation.Set, Number(1)),
            "multiply" => new MutateValueStep(new("bad"), new("value"), VariableMutation.Multiply, Number(2)),
            _ => new SubtreeStep(new("bad"), [TypedWrite("text", "local", new TextValue("true"))], outputs: [new(new("local"), new("value"))]),
        };
        var plan = b.Powered(new SequenceStep(new("body"), [TypedWrite("seed", "value", new BooleanValue(true)), invalid]));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(plan)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Theory]
    [InlineData(true, VariableMutation.Set)]
    [InlineData(true, VariableMutation.Multiply)]
    [InlineData(false, VariableMutation.Set)]
    [InlineData(false, VariableMutation.Multiply)]
    public async Task OutcomeMutationsRejectOptionalNonnumericBodyWritesBeforeIo(bool success, VariableMutation mutation)
    {
        var b = new Bench();
        ExperimentStep body = new IfElseStep(new("branch"), new(new("ready"), ComparisonOperator.Equal, new BooleanValue(true)),
            new SequenceStep(new("then"), [b.ReadField("observed"), TypedWrite("typed", "count", new BooleanValue(true))]), new DelayStep(new("else"), TimeSpan.Zero));
        if (!success) body = new ResultDecoratorStep(new("failed"), ResultEffect.ForceFailure, body);
        var action = new MutateValueStep(new("hook"), new("count"), mutation, Number(1));
        var hook = new OutcomeMutationStep(new("outcome"), body, success ? action : null, success ? null : action);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Powered(new SequenceStep(new("body"), [TypedWrite("seed", "ready", new BooleanValue(true)), hook]))));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task OutcomeMutationIgnoresPrivateSubtreeTypeShadowsThatCannotReachItsTarget()
    {
        var b = new Bench();
        var scope = new SubtreeStep(new("scope"), [TypedWrite("local", "count", new BooleanValue(false))], [new(new("ready"), new("count"))]);
        var hook = new OutcomeMutationStep(new("hook"), scope, new MutateValueStep(new("multiply"), new("count"), VariableMutation.Multiply, Number(2)));
        var plan = new ExperimentPlan("Local shadow", new SequenceStep(new("root"), [Local("seed", "count", Number(3)), TypedWrite("flag", "ready", new BooleanValue(true)), hook, Equal("six", "count", Number(6))]));
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(6, Assert.IsType<BlackboardQuantity>(b.Runs.LatestBlackboard![new("count")].Value).Value.Value);
        Assert.Equal(new StepId("multiply"), b.Runs.LatestBlackboard[new("count")].Source);
    }

    [Fact]
    public async Task FallbackAndRetryRestoreTypedEntryValuesAndOriginalProvenance()
    {
        var b = new Bench();
        var first = new SequenceStep(new("first"), [TypedWrite("mutate", "label", new TextValue("discarded")), TypedWrite("mutate_flag", "ready", new BooleanValue(false)), Check("fail", false)]);
        var attempt = new SequenceStep(new("attempt"), [TypedCheck("entry_label", "label", new TextValue("seed")), TypedCheck("entry_flag", "ready", new BooleanValue(true)), b.ReadField("row"),
            TypedWrite("next", "label", new TextValue("changed")), new ConditionStep(new("pass_on_second"), new(new("retry.attempt"), ComparisonOperator.Equal, new ConstantValue(Number(2))))]);
        var plan = new ExperimentPlan("Fallback retry", new SequenceStep(new("root"), [TypedWrite("label_seed", "label", new TextValue("seed")), TypedWrite("flag_seed", "ready", new BooleanValue(true)),
            Seed(new FallbackStep(new("choose"), [first, new RetryStep(new("retry"), 2, TimeSpan.Zero, attempt)]))]));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
        Assert.Equal(new StepId("flag_seed"), b.Runs.LatestBlackboard![new("ready")].Source);
        Assert.Equal("changed", Assert.IsType<BlackboardText>(b.Runs.LatestBlackboard[new("label")].Value).Value);
    }

    [Fact]
    public async Task NumericReadProvenanceSurvivesListenerFaultAndSnapshotsRemainRunLocal()
    {
        var b = new Bench(new RefusingListener());
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(new SequenceStep(new("body"), [TypedWrite("label", "sample", new TextValue("Si")), b.ReadSource("observed")])), Token));
        Assert.NotNull(error); var saved = b.Runs.LatestBlackboard!; var entry = saved[new("observed.value")];
        Assert.Equal(new StepId("observed"), entry.Source); Assert.Equal(PhysicalUnit.Ampere, Assert.IsType<BlackboardQuantity>(entry.Value).Value.Unit);
        Assert.Equal(BlackboardWriteKind.Step, entry.Kind); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await b.Runs.StartPlanAsync(new("Fresh", TypedWrite("new", "sample", new TextValue("Ge"))), Token);
        Assert.Equal("Si", Assert.IsType<BlackboardText>(saved[new("sample")].Value).Value);
        Assert.Equal("Ge", Assert.IsType<BlackboardText>(b.Runs.LatestBlackboard![new("sample")].Value).Value);
        Assert.False(b.Runs.LatestBlackboard.ContainsKey(new("observed.value")));
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly ParameterId Flag = new("flag");
    private static ExperimentStep Seed(ExperimentStep body) => new SequenceStep(new("seeded"), [new AssignValueStep(new("seed"), Flag, new ConstantValue(new(1, PhysicalUnit.Dimensionless))), body]);
    private static ConditionStep Check(string id, bool success) => new(new(id), new(Flag, ComparisonOperator.Equal, new ConstantValue(new(success ? 1 : 0, PhysicalUnit.Dimensionless))));

    [Theory]
    [InlineData(ResultEffect.ForceSuccess, true, true)]
    [InlineData(ResultEffect.ForceSuccess, false, true)]
    [InlineData(ResultEffect.ForceFailure, true, false)]
    [InlineData(ResultEffect.ForceFailure, false, false)]
    [InlineData(ResultEffect.Invert, true, false)]
    [InlineData(ResultEffect.Invert, false, true)]
    public async Task DecoratorsTransformOnlyExpectedResultsAndPublishStructuredOutcomes(ResultEffect effect, bool childSuccess, bool expectedSuccess)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var body = new SequenceStep(new("body"), [b.ReadField("observed"), Check("predicate", childSuccess)]);
        var plan = b.Powered(Seed(new ResultDecoratorStep(new("effect"), effect, body)));
        if (expectedSuccess) await b.Runs.StartPlanAsync(plan, Token);
        else Assert.Equal(new StepId("effect"), (await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(plan, Token))).Step);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(expectedSuccess ? MeasurementRunState.Completed : MeasurementRunState.Faulted, b.Runs.LatestDataSet.State);
        var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var fact)) facts.Add(fact);
        Assert.Equal(childSuccess ? StepOutcome.Success : StepOutcome.Failure, facts.Single(f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == new StepId("predicate")).Outcome);
        Assert.Equal(expectedSuccess ? StepOutcome.Success : StepOutcome.Failure, facts.Single(f => f.Kind == WorkbenchNotificationKind.StepCompleted && f.Step == new StepId("effect")).Outcome);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("repeat")]
    [InlineData("scan")]
    [InlineData("iterator")]
    [InlineData("branch")]
    public async Task LogicalFailureStopsParentBodyAndStillRunsIndependentCleanup(string kind)
    {
        var b = new Bench(); var failed = new SequenceStep(new("body"), [Check("fail", false), b.ReadField("unreachable")]);
        ExperimentStep parent = kind switch
        {
            "sequence" => new SequenceStep(new("parent"), [failed, b.ReadField("later")]),
            "repeat" => new RepeatStep(new("parent"), 3, failed),
            "scan" => new ScanStep(new("parent"), new(b.Field.Definition.Device.Id, b.Field.Definition.Target.Id), new("field_axis"), new([new(0, PhysicalUnit.Tesla), new(0.01, PhysicalUnit.Tesla)]), TimeSpan.Zero, failed),
            "iterator" => new ForEachStep(new("parent"), new("number"), new([new(0, PhysicalUnit.Dimensionless), new(1, PhysicalUnit.Dimensionless)]), failed),
            _ => new IfElseStep(new("parent"), new(Flag, ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))), failed, b.ReadField("other")),
        };
        Assert.Equal(new StepId("fail"), (await Assert.ThrowsAsync<PlanFailedException>(() => b.Runs.StartPlanAsync(b.Powered(Seed(parent)), Token))).Step);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Moving); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task FallbackKeepsFailedBranchRowsUsesEntryVariablesAndSkipsBranchesAfterSuccess()
    {
        var b = new Bench(); var fallback = new FallbackStep(new("choose"),
        [
            new SequenceStep(new("first"), [b.ReadField("first_read"), new AssignValueStep(new("mutate"), Flag, new ConstantValue(new(0, PhysicalUnit.Dimensionless))), Check("first_fail", true)]),
            new SequenceStep(new("second"), [Check("entry_value", true), b.ReadField("second_read")]),
            b.ReadField("unreachable"),
        ]);
        var plan = new ExperimentPlan("fallback", new SequenceStep(new("root"), [Seed(fallback), b.ReadField("after")]));
        await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!;
        Assert.Equal(3, data.Rows.Count); Assert.Equal(new[] { 1.0, 2.0 }, data.Rows.Take(2).Select(r => r.Values[fallback.BranchColumn].Value));
        Assert.All(data.Rows.Take(2), r => Assert.Equal(3, r.Values[fallback.TotalColumn].Value));
        Assert.False(data.Rows[^1].Values.ContainsKey(fallback.BranchColumn)); Assert.All(data.Rows, data.Schema.ValidateRow);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task RetryStopsAtFirstSuccessfulAttemptAndRetainsEachReadWithItsOwnAttemptAxes()
    {
        var b = new Bench(); var id = new StepId("retry");
        var body = new SequenceStep(new("attempt"), [b.ReadField("value"), new ConditionStep(new("ready"), new(new("retry.attempt"), ComparisonOperator.GreaterOrEqual, new ConstantValue(new(2, PhysicalUnit.Dimensionless))))]);
        var retry = new RetryStep(id, 4, TimeSpan.FromMilliseconds(20), body);
        await b.Runs.StartPlanAsync(new("retry", new SequenceStep(new("root"), [retry, b.ReadField("after")])), Token);
        var data = b.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count);
        Assert.Equal(new[] { 1.0, 2.0 }, data.Rows.Take(2).Select(r => r.Values[retry.AttemptColumn].Value));
        Assert.All(data.Rows.Take(2), r => Assert.Equal(4, r.Values[retry.TotalColumn].Value)); Assert.False(data.Rows[^1].Values.ContainsKey(retry.AttemptColumn));
        Assert.Contains(retry.AttemptColumn, data.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("value.value")).DependsOn);
    }

    [Fact]
    public async Task ExhaustedRetriesReturnFailureToFallbackWithoutRepeatingAThirdTime()
    {
        var b = new Bench(); var retry = new RetryStep(new("retry"), 2, TimeSpan.Zero, new SequenceStep(new("body"), [b.ReadField("attempt"), Check("fail", false)]));
        var fallback = new FallbackStep(new("choose"), [retry, b.ReadField("alternative")]);
        await b.Runs.StartPlanAsync(new("exhaust", Seed(fallback)), Token); var data = b.Runs.LatestDataSet!;
        Assert.Equal(3, data.Rows.Count); Assert.Equal(new[] { 1.0, 2.0 }, data.Rows.Take(2).Select(r => r.Values[retry.AttemptColumn].Value));
        Assert.Equal(2, data.Rows[^1].Values[fallback.BranchColumn].Value); Assert.False(data.Rows[^1].Values.ContainsKey(retry.AttemptColumn));
    }

    [Theory]
    [InlineData("force_success")]
    [InlineData("force_failure")]
    [InlineData("inverter")]
    [InlineData("fallback")]
    [InlineData("retry")]
    [InlineData("timeout")]
    [InlineData("subtree")]
    public async Task NativeTcpAcquisitionErrorIsNotSwallowedOrRetriedByWorkflowControl(string kind)
    {
        await using var peer = new Keithley2450ScpiPeer(); var measurements = 0;
        peer.Override = (command, _) =>
        {
            if (!command.StartsWith(":MEAS?", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref measurements); return Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("NaN,0,0"));
        };
        var source = new TcpKeithley2450SourceMeter(new("k2450"), "Local protocol source", peer.Server.Configuration()); var b = new Bench(source: source);
        var read = b.ReadSource("bad_read"); var controlled = Wrap(kind, read, b.ReadField("alternative"));
        var failure = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Powered(controlled), Token));
        Assert.IsType<IOException>(failure);
        Assert.Equal(1, measurements); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State);
        Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(source.Id, DateTimeOffset.UtcNow));
        await source.DisconnectAsync(Token);
    }

    private static ExperimentStep Wrap(string kind, ExperimentStep body, ExperimentStep alternative) => kind switch
    {
        "fallback" => new FallbackStep(new("wrapper"), [body, alternative]),
        "retry" => new RetryStep(new("wrapper"), 3, TimeSpan.Zero, body),
        "timeout" => new TimeoutStep(new("wrapper"), TimeSpan.FromSeconds(20), body),
        "subtree" => new SubtreeStep(new("wrapper"), [body]),
        _ => new ResultDecoratorStep(new("wrapper"), kind == "force_success" ? ResultEffect.ForceSuccess : kind == "force_failure" ? ResultEffect.ForceFailure : ResultEffect.Invert, body),
    };
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind, StepId? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var fact = await events.Reader.ReadAsync(deadline.Token); if (fact.Kind == kind && (step is null || fact.Step == step)) return; }
    }
    private static JsonObject Node(string type, JsonObject? p = null, params JsonObject[] children)
        => new() { ["type"] = type, ["params"] = p?.DeepClone() ?? new JsonObject(), ["children"] = new JsonArray(children.Select(c => c.DeepClone()).ToArray()) };
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public ISourceMeterPort Source { get; }
        public IAxisControllerPort Field { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public ExperimentImportBindings Bindings { get; }
        public Bench(IRunResultListener? listener = null, ISourceMeterPort? source = null, IAxisControllerPort? field = null)
        {
            Source = source ?? new MockKeithley2450SourceMeter(); Field = field ?? MockEnvironmentCatalog.Field();
            var manager = new InstrumentManager(Source, Hub); Registry = new([manager, new AxisControllerSession(Field, Hub)], Leases);
            Runs = new(manager, Leases, Hub, listener is null ? null : [listener], deviceRegistry: Registry);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
            Bindings = new([KeyValuePair.Create("k2450", Source.Id)], [KeyValuePair.Create("field", Field.Definition)]);
        }
        public ReadStep ReadField(string id) => new(new(id), [new(new(id + ".value"), new(Field.Definition.Device.Id, Field.Definition.Readback.Id))]);
        public ReadStep ReadSource(string id) => new(new(id), [new(new(id + ".value"), new(Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]);
        public ExperimentPlan Powered(ExperimentStep body) => new("powered control flow", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), body]),
            [new(Source.Id, SourceFunction.Voltage, new(0.01, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
    }
}

using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class ExperimentExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly ParameterId Voltage = new("voltage");
    private static readonly ParameterId Field = new("field_axis");
    private static readonly ParameterId Current = new("current");

    [Fact]
    public async Task FieldVoltageNestedScanAcquiresJointRowsAndHoldsInsteadOfZeroing()
    {
        var b = new Bench();
        var plan = b.Nested();
        await b.Runs.StartPlanAsync(plan, Token);
        var data = b.Runs.LatestDataSet!;
        Assert.Equal(9, data.Rows.Count);
        Assert.Equal(2, data.Instruments.Count);
        Assert.Equal(new[] { 0.1, 0.1, 0.1, 0.2, 0.2, 0.2, 0.3, 0.3, 0.3 }, data.Rows.Select(r => r.Values[Field].Value));
        Assert.Equal(new[] { -0.1, 0.0, 0.1, -0.1, 0.0, 0.1, -0.1, 0.0, 0.1 }, data.Rows.Select(r => r.Values[Voltage].Value));
        var currentColumn = data.Schema.Columns.Single(c => c.Parameter.Id == Current);
        Assert.Equal(new[] { Field, Voltage, new ParameterId("elapsed") }, currentColumn.DependsOn);
        Assert.Equal(new ParameterAddress(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id), currentColumn.Source);
        Assert.All(data.Rows, data.Schema.ValidateRow);
        var held = await b.Axis.GetSnapshotAsync(Token);
        Assert.False(held.Moving);
        Assert.Equal(held.Readback, held.Target);
        Assert.True(held.Readback.Value > 0);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
        Assert.False(b.Leases.IsLeased(b.Axis.Definition.Device.Id, DateTimeOffset.UtcNow));
        Assert.Single(b.Runs.History.Runs);
        var facts = Drain(b.Hub);
        Assert.Equal(9, facts.Count(n => n.Kind == WorkbenchNotificationKind.DataRowReceived));
        Assert.Contains(facts, n => n.Kind == WorkbenchNotificationKind.StepCompleted && n.Step == plan.Root.Id);
    }

    [Fact]
    public async Task RepeatIteratorAssignmentAndBranchesPreserveOptionalMeasurementRows()
    {
        var b = new Bench();
        var iterator = new ParameterId("iterator");
        var copy = new ParameterId("copy");
        var fieldRead = new ParameterId("field_read");
        var root = new RepeatStep(new("repeat"), 2, new ForEachStep(new("for_each"), iterator,
            new ScanValues([new(-0.1, PhysicalUnit.Volt), new(0.1, PhysicalUnit.Volt)]),
            new SequenceStep(new("body"),
            [
                new AssignValueStep(new("copy"), copy, new VariableValue(iterator)),
                new SetParameterStep(new("set"), b.SourceAddress(Keithley2450Parameters.SourceVoltage.Id), new VariableValue(copy)),
                new IfElseStep(new("branch"), new(copy, ComparisonOperator.Less, new ConstantValue(new(0, PhysicalUnit.Volt))),
                    b.ReadCurrent("negative"), new ReadStep(new("positive"), [new(fieldRead, b.ReadbackAddress)])),
            ])));
        await b.Runs.StartPlanAsync(b.Prepared(root), Token);
        var data = b.Runs.LatestDataSet!;
        Assert.Equal(4, data.Rows.Count);
        Assert.Equal(new[] { 1.0, 2.0, 1.0, 2.0 }, data.Rows.Select(r => r.Values[new("iterator.index")].Value));
        Assert.All(data.Rows, r => { Assert.Equal(2, r.Values[new("iterator.total")].Value); data.Schema.ValidateRow(r); });
        Assert.All(data.Rows.Where(r => r.Values[iterator].Value < 0), r => { Assert.True(r.Values.ContainsKey(Current)); Assert.False(r.Values.ContainsKey(fieldRead)); });
        Assert.All(data.Rows.Where(r => r.Values[iterator].Value > 0), r => { Assert.True(r.Values.ContainsKey(fieldRead)); Assert.False(r.Values.ContainsKey(Current)); });
        Assert.Equal(MeasurementRunState.Completed, data.State);
    }

    [Fact]
    public async Task PauseRestoresHeldTargetAndOnlyPreviouslyEnabledOutputs()
    {
        var b = new Bench();
        var offSource = new MockKeithley2450SourceMeter(new("second_source"));
        var offManager = new InstrumentManager(offSource, b.Hub);
        var registry = new DeviceRegistry([b.Manager, b.AxisSession, offManager], b.Leases);
        var runs = new MeasurementRunService(b.Manager, b.Leases, b.Hub, deviceRegistry: registry);
        var plan = b.Prepared(new SequenceStep(new("root"),
        [
            new SourceOutputStep(new("on"), b.Source.Id, true),
            new SetParameterStep(new("target"), b.TargetAddress, new ConstantValue(new(1, PhysicalUnit.Tesla))),
            new ReadStep(new("off_config"), [new(new("off_voltage"), new(offSource.Id, Keithley2450Parameters.SourceVoltage.Id))]),
            b.ReadCurrent("point"),
            new DelayStep(new("wait"), TimeSpan.FromSeconds(30)),
        ]));
        var task = runs.StartPlanAsync(plan, Token);
        await Until(b.Hub, WorkbenchNotificationKind.DataRowReceived, new("point"));
        runs.Pause();
        var paused = await Until(b.Hub, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        var held = await b.Axis.GetSnapshotAsync(Token);
        Assert.False(held.Moving);
        Assert.True(held.Readback.Value < 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.WriteAsync(b.Axis.Definition.Device.Id,
            b.Axis.Definition.Target.Id, new(0, PhysicalUnit.Tesla), Token));
        runs.Resume();
        var resumed = await Until(b.Hub, WorkbenchNotificationKind.RunResumed);
        Assert.Equal(paused.RunId, resumed.RunId);
        Assert.Equal(1, (await b.Axis.GetSnapshotAsync(Token)).Target.Value);
        Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False((await offSource.GetSnapshotAsync(Token)).OutputEnabled);
        runs.Abort();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeasurementRunState.Aborted, runs.LatestDataSet!.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False((await b.Axis.GetSnapshotAsync(Token)).Moving);
    }

    [Fact]
    public async Task StabilityWaitObservesReadbackAndTimeoutStillCleansEveryDevice()
    {
        var b = new Bench();
        var satisfied = new WaitStableStep(new("stable"), b.ReadbackAddress, new ConstantValue(new(0, PhysicalUnit.Tesla)),
            new(0.0001, PhysicalUnit.Tesla), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        await b.Runs.StartPlanAsync(new("satisfied", new SequenceStep(new("root"), [satisfied,
            new ReadStep(new("field"), [new(new("field_read"), b.ReadbackAddress)])])), Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        var timeout = new WaitStableStep(new("timeout"), b.ReadbackAddress, new ConstantValue(new(9, PhysicalUnit.Tesla)),
            new(0.0001, PhysicalUnit.Tesla), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(70));
        await Assert.ThrowsAsync<TimeoutException>(() => b.Runs.StartPlanAsync(b.Prepared(new SequenceStep(new("root"),
            [new SourceOutputStep(new("on"), b.Source.Id, true), timeout])), Token));
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False((await b.Axis.GetSnapshotAsync(Token)).Moving);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task AbortingNestedScanKeepsRowsAndSharedIvExecutorCanRunNext()
    {
        var b = new Bench();
        var task = b.Runs.StartPlanAsync(b.Nested(TimeSpan.FromSeconds(10)), Token);
        await Until(b.Hub, WorkbenchNotificationKind.DataRowReceived);
        b.Runs.Pause();
        await Until(b.Hub, WorkbenchNotificationKind.RunPaused);
        b.Runs.Abort();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State);
        Assert.False(b.Runs.HasActiveRun);
        await b.Runs.StartIvSweepAsync(new("next IV", 0, 0.1, 2, 105e-6, 0), Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
        Assert.Equal(2, b.Runs.History.Runs.Count);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("read_only")]
    [InlineData("unit")]
    [InlineData("unknown_device")]
    [InlineData("unbound")]
    [InlineData("branch_unbound")]
    [InlineData("duplicate_id")]
    [InlineData("duplicate_column")]
    [InlineData("budget")]
    [InlineData("unsupported")]
    public async Task WholePlanPreflightRejectsErrorsBeforeAnyLeaseOrDriverCall(string kind)
    {
        var b = new Bench();
        ExperimentStep bad = kind switch
        {
            "range" => new ScanStep(new("scan"), b.TargetAddress, Field,
                new ScanValues([new(0, PhysicalUnit.Tesla), new(10, PhysicalUnit.Tesla)]), TimeSpan.Zero, b.ReadCurrent()),
            "read_only" => new SetParameterStep(new("set"), b.ReadbackAddress, new ConstantValue(new(1, PhysicalUnit.Tesla))),
            "unit" => new SetParameterStep(new("set"), b.TargetAddress, new ConstantValue(new(1, PhysicalUnit.Volt))),
            "unknown_device" => new SourceOutputStep(new("on"), new("missing"), true),
            "unbound" => new SetParameterStep(new("set"), b.TargetAddress, new VariableValue(new("missing"))),
            "branch_unbound" => new SequenceStep(new("root"),
            [new AssignValueStep(new("known"), new("condition"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
             new IfElseStep(new("branch"), new(new("condition"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
                 new AssignValueStep(new("maybe"), new("maybe"), new ConstantValue(new(1, PhysicalUnit.Tesla)))),
             new SetParameterStep(new("set"), b.TargetAddress, new VariableValue(new("maybe")))]),
            "duplicate_id" => new SequenceStep(new("root"), [new DelayStep(new("same"), TimeSpan.Zero), new DelayStep(new("same"), TimeSpan.Zero)]),
            "duplicate_column" => new SequenceStep(new("root"), [b.ReadCurrent(), new ReadStep(new("field"), [new(Current, b.ReadbackAddress)])]),
            "budget" => new RepeatStep(new("repeat"), 1_000_000, new RepeatStep(new("nested"), 1_000_000, new DelayStep(new("wait"), TimeSpan.Zero))),
            _ => new UnknownStep(new("unknown")),
        };
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Prepared(bad), Token)));
        Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.False((await b.Axis.GetSnapshotAsync(Token)).Connected);
        Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        Assert.Empty(Drain(b.Hub));
    }

    [Fact]
    public async Task VariableRangeValidationAtRuntimeStopsOutputAndRetainsPartialData()
    {
        var b = new Bench();
        var iterator = new ParameterId("supply");
        var body = new SequenceStep(new("body"),
        [new SetParameterStep(new("set"), b.SourceAddress(Keithley2450Parameters.SourceVoltage.Id), new VariableValue(iterator)), b.ReadCurrent()]);
        var root = new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true),
            new ForEachStep(new("loop"), iterator, new ScanValues([new(0.1, PhysicalUnit.Volt), new(211, PhysicalUnit.Volt)]), body)]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(b.Prepared(root), Token));
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task CommandOnlyPlanHasNoArtificialDeviceLeaseOrMeasurementRow()
    {
        var b = new Bench();
        await b.Runs.StartPlanAsync(new("commands", new RepeatStep(new("repeat"), 2,
            new AssignValueStep(new("assign"), new("value"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))))), Token);
        Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.Empty(b.Runs.LatestDataSet.Instruments);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task LargeCommandOnlyLoopYieldsAndAbortCanInterruptIt()
    {
        var b = new Bench();
        var task = b.Runs.StartPlanAsync(new("commands", new RepeatStep(new("repeat"), 100_000,
            new AssignValueStep(new("assign"), new("value"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))))), Token);
        Assert.False(task.IsCompleted);
        b.Runs.Abort();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task FailedHardwareConnectionRetainsGroupUntilExplicitReconnectAndStop()
    {
        var b = new Bench();
        var port = new FaultingHardwareAxis(b.Axis) { ConnectError = new IOException("transport unavailable") };
        var registry = new DeviceRegistry([b.Manager, new AxisControllerSession(port, b.Hub)], b.Leases);
        var runs = new MeasurementRunService(b.Manager, b.Leases, b.Hub, deviceRegistry: registry);
        var plan = b.Prepared(new ReadStep(new("read"), [new(new("field_read"), b.ReadbackAddress)]));
        var error = await Assert.ThrowsAsync<AggregateException>(() => runs.StartPlanAsync(plan, Token));
        Assert.IsType<IOException>(error.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(error.InnerExceptions[1]);
        Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, runs.Phase);
        Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1)));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.RecoverSafetyAsync(Token));
        port.ConnectError = null;
        await runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.False(runs.HasActiveRun);
        Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await runs.StartPlanAsync(plan, Token);
        Assert.Single(runs.LatestDataSet!.Rows);
    }

    // Exercises the hardware policy without claiming a real transport test.
    private sealed class FaultingHardwareAxis : IAxisControllerPort
    {
        private readonly MockAxisController inner;
        public FaultingHardwareAxis(MockAxisController inner)
        {
            this.inner = inner;
            var d = inner.Definition;
            Definition = new(new(d.Device.Id, d.Device.Model, d.Device.DisplayName, DriverKind.Hardware,
                d.Device.StopPolicy, d.Device.Parameters), d.Target, d.Readback, d.Rate);
        }
        public Exception? ConnectError { get; set; }
        public AxisControllerDefinition Definition { get; }
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => ConnectError is { } error
            ? Task.FromException<AxisControllerSnapshot>(error) : inner.ConnectAsync(token);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity value, CancellationToken token) => inner.SetTargetAsync(value, token);
        public Task SetRateAsync(Quantity value, CancellationToken token) => inner.SetRateAsync(value, token);
        public Task HoldAsync(CancellationToken token) => inner.HoldAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }

    private sealed record UnknownStep(StepId StepId) : ExperimentStep(StepId);

    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new();
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockAxisController Axis { get; } = MockEnvironmentCatalog.Field();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public InstrumentManager Manager { get; }
        public AxisControllerSession AxisSession { get; }
        public MeasurementRunService Runs { get; }
        public Bench()
        {
            Manager = new(Source, Hub); AxisSession = new(Axis, Hub);
            Runs = new(Manager, Leases, Hub, deviceRegistry: new DeviceRegistry([Manager, AxisSession], Leases));
        }
        public ParameterAddress SourceAddress(ParameterId id) => new(Source.Id, id);
        public ParameterAddress TargetAddress => new(Axis.Definition.Device.Id, Axis.Definition.Target.Id);
        public ParameterAddress ReadbackAddress => new(Axis.Definition.Device.Id, Axis.Definition.Readback.Id);
        public ReadStep ReadCurrent(string id = "current_read") => new(new(id), [new(Current, SourceAddress(Keithley2450Parameters.MeasuredCurrent.Id))]);
        public ExperimentPlan Prepared(ExperimentStep root) => new("plan", root,
            [new(Source.Id, Core.Instruments.SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere),
                new(Core.Instruments.SenseFunction.Current, Core.Instruments.SensingMode.FourWire, 1, true))]);
        public ExperimentPlan Nested(TimeSpan? dwell = null) => Prepared(new SequenceStep(new("root"),
        [new SourceOutputStep(new("on"), Source.Id, true),
         new ScanStep(new("field_scan"), TargetAddress, Field, ScanValues.Linear(new(0.1, PhysicalUnit.Tesla), new(0.3, PhysicalUnit.Tesla), 3), TimeSpan.Zero,
             new ScanStep(new("voltage_scan"), SourceAddress(Keithley2450Parameters.SourceVoltage.Id), Voltage,
                 ScanValues.Linear(new(-0.1, PhysicalUnit.Volt), new(0.1, PhysicalUnit.Volt), 3), dwell ?? TimeSpan.Zero,
                 new ReadStep(new("joint"), [new(Current, SourceAddress(Keithley2450Parameters.MeasuredCurrent.Id)), new(new("field_read"), ReadbackAddress)])))]));
    }

    private static async Task<WorkbenchNotification> Until(NotificationHub hub, WorkbenchNotificationKind kind, StepId? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var fact = await hub.Reader.ReadAsync(deadline.Token);
            if (fact.Kind == kind && (step is null || step == fact.Step)) return fact;
        }
    }
    private static List<WorkbenchNotification> Drain(NotificationHub hub)
    {
        var result = new List<WorkbenchNotification>();
        while (hub.Reader.TryRead(out var fact)) result.Add(fact);
        return result;
    }
}

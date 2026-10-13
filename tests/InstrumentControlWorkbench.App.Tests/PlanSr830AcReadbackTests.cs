using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanSr830AcReadbackTests
{
    private static readonly MatrixCrossPoint A = new(1, 1, 1), B = new(1, 1, 2);
    private static MatrixSwitchDefinition Definition(string id = "isolator") => new(new(new(id), "MATRIX", "Isolation simulation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 2, 2)], [], new(new("generator"), [A, B], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)));
    private static DeviceStatus Status(MatrixSwitchDefinition definition, DateTimeOffset time, bool connected = true, bool known = true, MatrixCrossPoint[]? closed = null, bool includeMatrix = true, bool safety = false, InstrumentLifecycleState lifecycle = InstrumentLifecycleState.Idle)
        => new(definition.Device, lifecycle, connected, safety, time, new Dictionary<ParameterId, Quantity>(), matrix: includeMatrix ? new(definition, connected, known, closed ?? [], time) : null);
    [Theory]
    [InlineData("open", AcIsolationReadbackState.Open)]
    [InlineData("closed", AcIsolationReadbackState.Closed)]
    [InlineData("mixed", AcIsolationReadbackState.Mixed)]
    [InlineData("disconnected", AcIsolationReadbackState.Disconnected)]
    [InlineData("unknown", AcIsolationReadbackState.Unknown)]
    [InlineData("missing", AcIsolationReadbackState.Unknown)]
    [InlineData("faulted", AcIsolationReadbackState.Unknown)]
    public void StateUsesTypedRouteReadbackRatherThanGenericSafetyFlag(string name, AcIsolationReadbackState expected)
    {
        var definition = Definition(); var row = new PlanSr830AcIsolationReadbackViewModel(definition); var time = DateTimeOffset.UtcNow.AddSeconds(-1);
        row.Apply(Status(definition, time, connected: name != "disconnected", known: name != "unknown", closed: name == "closed" ? [A, B] : name == "mixed" ? [A] : [], includeMatrix: name != "missing", lifecycle: name == "faulted" ? InstrumentLifecycleState.Faulted : InstrumentLifecycleState.Idle));
        Assert.Equal(expected, row.State); Assert.NotEqual("—", row.ObservedAt);
    }
    [Fact]
    public void StaleOrDuplicateFactsCannotOverwriteNewerRouteAndEpochInvalidationRequiresFreshReadback()
    {
        var definition = Definition(); var row = new PlanSr830AcIsolationReadbackViewModel(definition); var t = DateTimeOffset.UtcNow.AddSeconds(-3);
        row.Apply(Status(definition, t, closed: [A, B])); row.Apply(Status(definition, t.AddSeconds(1), closed: []));
        row.Apply(Status(definition, t, closed: [A, B])); row.Apply(Status(definition, t.AddSeconds(1), closed: [A, B])); Assert.Equal(AcIsolationReadbackState.Open, row.State);
        row.Invalidate(t, "旧生命周期通知"); Assert.Equal(AcIsolationReadbackState.Open, row.State);
        row.Invalidate(t.AddSeconds(2), "正在确认"); row.Apply(Status(definition, t.AddSeconds(1), closed: [])); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        row.Apply(Status(definition, t.AddSeconds(2.5), closed: [A])); Assert.Equal(AcIsolationReadbackState.Mixed, row.State);
    }
    [Fact]
    public void ForeignDefinitionFutureTimeAndMissingSnapshotCannotReuseOpenStatus()
    {
        var definition = Definition(); var row = new PlanSr830AcIsolationReadbackViewModel(definition); var t = DateTimeOffset.UtcNow.AddSeconds(-3);
        row.Apply(Status(definition, t)); Assert.Equal(AcIsolationReadbackState.Open, row.State);
        row.Apply(Status(Definition(), t.AddSeconds(1))); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        row.Apply(Status(definition, t.AddSeconds(2))); Assert.Equal(AcIsolationReadbackState.Open, row.State);
        row.Apply(Status(definition, DateTimeOffset.UtcNow.AddMinutes(1))); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        row.Apply(Status(definition, DateTimeOffset.UtcNow.AddMilliseconds(-1), includeMatrix: false)); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
    }
    [Fact]
    public async Task UnscopedDeviceFactsRemainSeparateFromPlanDraftsAndLifecycleDoesNotInventIsolation()
    {
        var hub = new NotificationHub(); var meter = new MockSr830ExcitationMeter(new("generator"), "Simulation"); var definition = Definition();
        var registry = new DeviceRegistry([new Sr830MeterSession(meter, hub), new MatrixSwitchSession(new MockMatrixSwitch(definition), hub)], new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
        var plan = new ExperimentPlan("Active", new ReadSr830AcFrameStep(new("ac"), meter.Descriptor.Id, definition.Device.Id, [A, B], new(new(17, PhysicalUnit.Hertz), new(.01, PhysicalUnit.Volt)), Sr830InputMode.VoltageAB, new("raw"), TimeSpan.Zero));
        var edits = 0; await using var vm = new PlanWorkspaceViewModel(workspace, plan, "plan.json", () => true, () => edits++);
        var initialEdits = edits; var document = vm.DocumentText; var row = Assert.Single(vm.Sr830AcIsolationReadbacks); var t = DateTimeOffset.UtcNow.AddSeconds(-3);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.DeviceStateChanged, "closed", Device: Status(definition, t, closed: [A, B]))); Assert.Equal(AcIsolationReadbackState.Closed, row.State);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.RunStarted, "start", RunId: "run", Timestamp: t.AddSeconds(1))); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.DeviceStateChanged, "open", Device: Status(definition, t.AddSeconds(2)))); Assert.Equal(AcIsolationReadbackState.Open, row.State);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.RunCompleted, "completed", RunId: "run")); Assert.Equal(AcIsolationReadbackState.Open, row.State);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.RunFinalizing, "confirming", RunId: "run", Timestamp: t.AddSeconds(2.5))); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.RunCompleted, "completed", RunId: "run")); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        vm.ApplyAcIsolationFact(new(WorkbenchNotificationKind.DeviceStateChanged, "open", Device: Status(definition, DateTimeOffset.UtcNow.AddMilliseconds(-1))));
        vm.InvalidateAcIsolationObservation(); Assert.Equal(AcIsolationReadbackState.Unknown, row.State);
        Assert.Equal(document, vm.DocumentText); Assert.Equal(initialEdits, edits); Assert.False(vm.HasUnappliedEdits); Assert.False((await meter.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
}

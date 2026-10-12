using InstrumentControlWorkbench.App.ViewModels;
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
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanOperatorTests
{
    [Fact]
    public async Task EditingInvalidatesRunAndValidationRestoresItWithoutConnecting()
    {
        var (vm, _, source, _) = Create();
        await using var owner = vm;
        var text = vm.Plans!.DocumentText;
        Assert.True(vm.StartPlanCommand.CanExecute(null));
        vm.Plans.DocumentText = "bad JSON";
        Assert.False(vm.StartPlanCommand.CanExecute(null));
        vm.Plans.ValidateCommand.Execute(null);
        Assert.True(vm.Plans.HasError);
        Assert.Equal("bad JSON", vm.Plans.DocumentText);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.Plans.DocumentText = text;
        vm.Plans.ValidateCommand.Execute(null);
        Assert.True(vm.Plans.IsValidated);
        Assert.True(vm.StartPlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedLoadRetainsDraftAndSuccessfulSaveClearsDirtyState()
    {
        var (vm, _, _, _) = Create();
        await using var owner = vm;
        var directory = Path.Combine(Path.GetTempPath(), $"icw-ui-{Guid.NewGuid():N}");
        try
        {
            var text = vm.Plans!.DocumentText;
            vm.Plans.DocumentPath = Path.Combine(directory, "plan.json");
            await vm.Plans.LoadCommand.ExecuteAsync(null);
            Assert.True(vm.Plans.HasError);
            Assert.Equal(text, vm.Plans.DocumentText);
            await vm.Plans.SaveCommand.ExecuteAsync(null);
            Assert.False(vm.Plans.HasError);
            Assert.False(vm.Plans.IsDirty);
            vm.Plans.DocumentText = "bad draft";
            await vm.Plans.LoadCommand.ExecuteAsync(null);
            Assert.True(vm.Plans.IsValidated);
            Assert.False(vm.Plans.IsDirty);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PlanExecutionLocksFileCommandsAndPauseResumeUseTheSharedBackend()
    {
        var (vm, runs, source, hub) = Create();
        await using var owner = vm;
        using var events = hub.Subscribe();
        var task = vm.StartPlanCommand.ExecuteAsync(null);
        await Until(events, WorkbenchNotificationKind.DataRowReceived);
        Assert.False(vm.Plans!.CanEdit);
        Assert.False(vm.Plans.SaveCommand.CanExecute(null));
        Assert.False(vm.StartRunCommand.CanExecute(null));
        Assert.True(vm.PauseRunCommand.CanExecute(null));
        vm.PauseRunCommand.Execute(null);
        Assert.False(vm.PauseRunCommand.CanExecute(null));
        await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.True(vm.ResumeRunCommand.CanExecute(null));
        vm.ResumeRunCommand.Execute(null);
        await Until(events, WorkbenchNotificationKind.RunResumed);
        vm.AbortRunCommand.Execute(null);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MeasurementRunState.Aborted, runs.LatestOutcome!.State);
        Assert.True(vm.Plans.CanEdit);
        Assert.False(vm.ResumeRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task CloseAbortsAPlanAndWaitsForOutputOffBeforeDisposing()
    {
        var (vm, runs, source, hub) = Create();
        using var events = hub.Subscribe();
        var task = vm.StartPlanCommand.ExecuteAsync(null);
        await Until(events, WorkbenchNotificationKind.DataRowReceived);
        await vm.DisposeAsync();
        await task;
        Assert.False(runs.HasActiveRun);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.False(vm.StartPlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedCleanupPreventsCloseAndExposesRecoveryUnderRetainedOwnership()
    {
        var hub = new NotificationHub();
        var port = new StopFaultDriver();
        var manager = new InstrumentManager(port, hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub);
        var vm = new MainWindowViewModel(manager, runs, hub);
        vm.DwellSecondsText = "0.02"; vm.SweepPointsText = "2";
        await vm.StartRunCommand.ExecuteAsync(null);
        Assert.True(runs.HasActiveRun);
        Assert.True(vm.NeedsSafetyRecovery);
        Assert.False(vm.StartRunCommand.CanExecute(null));
        await Assert.ThrowsAsync<IOException>(() => vm.DisposeAsync().AsTask());
        Assert.True(vm.RecoverSafetyCommand.CanExecute(null));
        Assert.Contains("关闭未完成", vm.OperationError);
        port.StopFails = false;
        await vm.RecoverSafetyCommand.ExecuteAsync(null);
        Assert.False(runs.HasActiveRun);
        Assert.False(vm.NeedsSafetyRecovery);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PlanTraceCountsCommittedRowsAndIgnoresOtherRunIds()
    {
        var (vm, _, _, _) = Create();
        await using var owner = vm;
        var planVm = vm.Plans!;
        planVm.BeginRun();
        planVm.ApplyFact(new(WorkbenchNotificationKind.RunStarted, "started", RunId: "run_a"));
        planVm.ApplyFact(new(WorkbenchNotificationKind.StepStarted, "step", RunId: "run_a", Step: new("read")));
        Assert.Equal("read", planVm.CurrentStep);
        var row = new MeasurementDataRow(DateTimeOffset.UnixEpoch, new Dictionary<ParameterId, Quantity> { [new("current")] = new(1e-6, PhysicalUnit.Ampere) });
        planVm.ApplyFact(new(WorkbenchNotificationKind.DataRowReceived, "foreign", RunId: "run_b", Row: row));
        Assert.Empty(planVm.Rows);
        for (var i = 0; i < 205; i++) planVm.ApplyFact(new(WorkbenchNotificationKind.DataRowReceived, "row", RunId: "run_a", Row: row));
        Assert.Equal(205, planVm.AcquiredRows);
        Assert.Equal(200, planVm.Rows.Count);
        Assert.Equal("205", planVm.Rows[0].Index);
        Assert.Contains("Ampere", planVm.Rows[0].Values);
        planVm.ApplyFact(new(WorkbenchNotificationKind.DataRowReceived, "latest after gap", RunId: "run_a", Row: row, AcquiredRowCount: 300));
        Assert.Equal(300, planVm.AcquiredRows); Assert.Equal("300", planVm.Rows[0].Index);
        planVm.ApplyFact(new(WorkbenchNotificationKind.RunCompleted, "terminal after gap", RunId: "run_a", AcquiredRowCount: 350));
        Assert.Equal(350, planVm.AcquiredRows); Assert.Equal(200, planVm.Rows.Count);
    }

    private static (MainWindowViewModel, MeasurementRunService, MockKeithley2450SourceMeter, NotificationHub) Create()
    {
        var hub = new NotificationHub();
        var source = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(source, hub);
        var field = MockEnvironmentCatalog.Field();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager, new AxisControllerSession(field, hub)], leases);
        var runs = new MeasurementRunService(manager, leases, hub, deviceRegistry: registry);
        var plan = new ExperimentPlan("operator", new SequenceStep(new("root"),
            [new SourceOutputStep(new("on"), source.Id, true),
             new ReadStep(new("read"), [new(new("current"), new(source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]),
             new DelayStep(new("wait"), TimeSpan.FromSeconds(30))]),
            [new(source.Id, SourceFunction.Voltage, new(0.02, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere),
                new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        var vm = new MainWindowViewModel(manager, runs, hub, new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs),
            plan, Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.json"));
        return (vm, runs, source, hub);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class StopFaultDriver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public bool StopFails { get; set; } = true;
        public InstrumentId Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint source, CancellationToken token) => inner.ConfigureSourceAsync(source, token);
        public Task ConfigureSenseAsync(SenseConfiguration sense, CancellationToken token) => inner.ConfigureSenseAsync(sense, token);
        public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token);
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token) => inner.ReadSourceSenseAsync(token);
        public Task SafeStopAsync(CancellationToken token) => StopFails ? Task.FromException(new IOException("stop unavailable")) : inner.SafeStopAsync(token);
    }
}

using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
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
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class SourceIndependentExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static string PlanFile(string name) => Path.Combine(AppContext.BaseDirectory, "plans", name);
    private static async Task<ConfiguredDeviceRuntime> Runtime(NotificationHub hub, InstrumentLeaseService leases)
    {
        var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", "axes-only-devices.json"), Token);
        return new(catalog, new ConfiguredDeviceFactory(), leases, hub);
    }
    private static async Task<WorkbenchNotification> Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var item = await events.Reader.ReadAsync(deadline.Token); if (item.Kind == kind) return item; }
    }
    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 24)]
    public async Task ActualNativeAndPpmsFilesExecuteArchiveExportAndReexecuteWithoutAnySource(bool ppms, int rows)
    {
        var folder = Path.Combine(Path.GetTempPath(), "axes-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var runtime = await Runtime(hub, leases);
            Assert.Empty(runtime.Sources); Assert.Null(runtime.Configuration.PrimarySource);
            await using var archive = new FileRunArchive(Path.Combine(folder, "archive")); var runs = new MeasurementRunService(runtime.Registry, hub, [archive]);
            var w = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter());
            var document = ppms ? await w.ImportAsync(PlanFile("ppms-axes-only-import.json"), runtime.CreateImportBindings(), Token) : await w.LoadAsync(PlanFile("axes-only-scan.plan.json"), Token);
            Assert.Equal(3, document.Preview.Devices.Count); Assert.Empty(document.Plan.Preparations);
            var saved = Path.Combine(folder, "plan.json"); await w.SaveAsync(saved, document.Text, Token); document = await w.LoadAsync(saved, Token);
            using var events = hub.Subscribe(); await runs.StartPlanAsync(document.Plan, Token); var data = runs.LatestDataSet!;
            Assert.Equal(rows, data.Rows.Count); Assert.Equal(3, data.Instruments.Count); Assert.Equal(MeasurementRunState.Completed, data.State);
            Assert.DoesNotContain(data.Schema.Columns, c => c.Parameter.Unit is PhysicalUnit.Volt or PhysicalUnit.Ampere or PhysicalUnit.Ohm);
            Assert.All(data.Instruments, i => Assert.Contains(i.InstrumentId, document.Preview.Devices));
            if (ppms)
            {
                Assert.Equal(6, data.Rows.Count(row => row.MeasurementGroup is not null));
                Assert.All(data.Rows, row => Assert.All(row.Environment, e => { Assert.Null(e.Error); Assert.Contains(e.Source!.Device, document.Preview.Devices); }));
            }
            foreach (var device in runtime.Registry.Devices)
            { Assert.True((await runtime.Registry.GetStatusAsync(device.Id, Token)).SafetyConfirmed); Assert.False(leases.IsLeased(device.Id, DateTimeOffset.UtcNow)); }
            var facts = new List<WorkbenchNotification>(); while (events.Reader.TryRead(out var n)) facts.Add(n);
            Assert.All(facts.Where(n => n.Kind is WorkbenchNotificationKind.RunStarted or WorkbenchNotificationKind.RunFinalizing or WorkbenchNotificationKind.RunCompleted), n => { Assert.Null(n.InstrumentId); Assert.Equal(data.Id.ToString(), n.RunId); });
            await using var reader = new FileRunArchive(Path.Combine(folder, "archive")); var stored = await reader.LoadAsync(data.Id, Token); Assert.Equal(rows, stored.DataSet.Rows.Count);
            Assert.Equal(document.Text, new JsonExperimentPlanDocument().Serialize(stored.DataSet.Plan!)); var csv = Path.Combine(folder, "data.csv"); await reader.ExportCsvAsync(data.Id, csv, Token);
            Assert.DoesNotContain("SourceMeter", await File.ReadAllTextAsync(csv));
            var freshHub = new NotificationHub(); var fresh = await Runtime(freshHub, new(TimeSpan.FromSeconds(5))); var replay = new MeasurementRunService(fresh.Registry, freshHub);
            await replay.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(rows, replay.LatestDataSet!.Rows.Count); Assert.Empty(fresh.Sources);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseResumeOrPausedAbortPreservesOnlyActualAxisOwnership(bool resume)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(longDelay: true), Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); var paused = await Until(events, WorkbenchNotificationKind.RunPaused); Assert.Null(paused.InstrumentId);
            foreach (var port in b.Ports) { var snapshot = await port.Inner.GetSnapshotAsync(Token); Assert.False(snapshot.Moving); Assert.Equal(snapshot.Readback, snapshot.Target); Assert.True(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1))); }
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Field.Definition.Device.Id, b.Field.Definition.Target.Id, new(0, PhysicalUnit.Tesla), Token));
            if (resume)
            {
                b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); Assert.Equal(1, (await b.Field.Inner.GetSnapshotAsync(Token)).Target.Value);
            }
            b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Single(b.Runs.LatestDataSet.Rows);
            foreach (var port in b.Ports) { Assert.False((await port.Inner.GetSnapshotAsync(Token)).Moving); Assert.False(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow)); }
        }
        finally { if (!running.IsCompleted) { b.Runs.Abort(); await running; } }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryReadFailureAndIndependentHoldFailurePreserveEvidenceAndRecovery(bool failedHold)
    {
        var listener = new Listener(); var b = new Bench(listener); listener.FirstRow = () => b.Field.FailNextSnapshot = true;
        if (failedHold) b.Field.HoldError = new IOException("field hold failed");
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(b.Plan(twoReads: true), Token)); Assert.NotNull(error);
        var errors = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [error];
        Assert.Contains(errors, e => e!.Message.Contains("field read failed", StringComparison.Ordinal));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.Single(b.Runs.History.Runs);
        Assert.Equal(1, b.Temperature.Holds); Assert.Equal(1, b.Angle.Holds);
        if (failedHold)
        {
            Assert.Contains(errors, e => e!.Message.Contains("field hold failed", StringComparison.Ordinal)); Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
            foreach (var port in b.Ports) Assert.True(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(), Token)); await b.Runs.WaitForFinalizationAsync(Token);
            b.Field.HoldError = null; await b.Runs.RecoverSafetyAsync(Token); Assert.Equal(2, b.Field.Holds); Assert.Equal(1, b.Temperature.Holds); Assert.Equal(1, b.Angle.Holds);
        }
        Assert.False(b.Runs.HasActiveRun); foreach (var port in b.Ports) Assert.False(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow));
        listener.FirstRow = null; await b.Runs.StartPlanAsync(b.Plan(), Token); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizationStillOwnsAxesUntilAllResultListenersFinish(bool failListener)
    {
        var listener = new Listener { BlockFinalization = true, FailFinalization = failListener }; var b = new Bench(listener); var running = b.Runs.StartPlanAsync(b.Plan(), Token);
        try
        {
            await listener.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.Finalizing, b.Runs.Phase);
            var finalized = b.Runs.WaitForFinalizationAsync(Token); Assert.False(finalized.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(), Token));
            foreach (var port in b.Ports) { Assert.False((await port.Inner.GetSnapshotAsync(Token)).Moving); Assert.True(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow)); }
            listener.Release.TrySetResult();
            if (failListener) await Assert.ThrowsAsync<IOException>(() => running); else await running;
            await finalized; Assert.False(b.Runs.HasActiveRun); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestDataSet!.State);
            Assert.Equal(failListener ? MeasurementRunState.Faulted : MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
            foreach (var port in b.Ports) Assert.False(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow));
        }
        finally { listener.Release.TrySetResult(); if (!running.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => running); } }
    }
    [Fact]
    public async Task PrecanceledAndInvalidPlansNeverConnectAControllerOrLeakClaims()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await b.Runs.StartPlanAsync(b.Plan(), cancelled.Token); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Null(b.Runs.LatestDataSet);
        foreach (var port in b.Ports) { Assert.Equal(0, port.Connects); Assert.Equal(0, port.Holds); Assert.False(b.Leases.IsLeased(port.Definition.Device.Id, DateTimeOffset.UtcNow)); }
        var invalid = new ExperimentPlan("Out of range", new SetParameterStep(new("invalid"), new(b.Field.Definition.Device.Id, b.Field.Definition.Target.Id), new ConstantValue(new(10, PhysicalUnit.Tesla))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(invalid, Token)); foreach (var port in b.Ports) Assert.Equal(0, port.Connects);
        var facts = new List<WorkbenchNotification>();
        while (events.Reader.TryRead(out var fact)) facts.Add(fact);
        Assert.Contains(facts, fact => fact.Kind == WorkbenchNotificationKind.RunAborted);
        Assert.DoesNotContain(facts, fact => fact.Kind is WorkbenchNotificationKind.RunStarted or WorkbenchNotificationKind.DeviceStateChanged);
        Assert.All(facts, fact => Assert.Equal(b.Runs.LatestOutcome!.Id.ToString(), fact.RunId));
    }
    [Fact]
    public async Task SmuShortcutsRequireSelectionButExplicitSourcePlanDoesNot()
    {
        var hub = new NotificationHub(); var first = new MockKeithley2450SourceMeter(new("first")); var second = new MockKeithley2450SourceMeter(new("second")); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([new InstrumentManager(first, hub), new InstrumentManager(second, hub)], leases); var runs = new MeasurementRunService(registry, hub);
        var recipe = new IvSweepRecipe("Explicit second source", 0, 0.1, 3, 105e-6, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.StartIvSweepAsync(recipe, Token)); await Assert.ThrowsAsync<InvalidOperationException>(() => runs.StartVoltageResistanceAsync(new("R", new(0.01, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere)), Token));
        Assert.False((await first.GetSnapshotAsync(Token)).Connected); Assert.False((await second.GetSnapshotAsync(Token)).Connected);
        var plan = new ExperimentPlan(recipe.Name, new SequenceStep(new("root"),
        [
            new SourceOutputStep(new("output"), second.Id, true),
            new ScanStep(new("scan"), new(second.Id, Keithley2450Parameters.SourceVoltage.Id), new("voltage"),
                ScanValues.Linear(new(0, PhysicalUnit.Volt), new(0.1, PhysicalUnit.Volt), 3), TimeSpan.Zero,
                new ReadSourceSenseStep(new("read"), second.Id, SourceFunction.Voltage, new("voltage_actual"), new("current_actual"))),
        ]), [new(second.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        using var events = hub.Subscribe(); await runs.StartPlanAsync(plan, Token);
        Assert.Equal(second.Id, (await Until(events, WorkbenchNotificationKind.RunCompleted)).InstrumentId); Assert.Equal(3, runs.LatestDataSet!.Rows.Count);
        Assert.Equal(second.Id, Assert.Single(runs.LatestDataSet.Instruments).InstrumentId); Assert.False((await first.GetSnapshotAsync(Token)).Connected); Assert.False((await second.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task LegacyPrimaryThatIsNotInThePlanIsNeitherTouchedNorNamedInRunEvents()
    {
        var b = new Bench(); var source = new MockKeithley2450SourceMeter(new("foreign-smu")); await source.ConnectAsync(Token); await source.EnableOutputAsync(Token);
        var manager = new InstrumentManager(source, b.Hub); var registry = new DeviceRegistry([manager, new AxisControllerSession(b.Field, b.Hub)], b.Leases);
        b.Leases.AcquireExclusive(source.Id, "foreign", DateTimeOffset.UtcNow); var runs = new MeasurementRunService(manager, b.Leases, b.Hub, deviceRegistry: registry);
        using var events = b.Hub.Subscribe();
        try
        {
            var read = new ReadStep(new("field"), [new(new("actual"), new(b.Field.Definition.Device.Id, b.Field.Definition.Readback.Id))]); await runs.StartPlanAsync(new("Field only with unrelated source", read), Token);
            Assert.Equal(b.Field.Definition.Device.Id, (await Until(events, WorkbenchNotificationKind.RunCompleted)).InstrumentId);
            Assert.Equal(b.Field.Definition.Device.Id, Assert.Single(runs.LatestDataSet!.Instruments).InstrumentId); Assert.True((await source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Leases.IsLeased(source.Id, DateTimeOffset.UtcNow));
        }
        finally { await source.SafeStopAsync(Token); b.Leases.Release(source.Id, "foreign"); }
    }
    [Fact]
    public async Task ResourceFreeControlPlanStillPausesAbortsArchivesAndFinalizesWithoutIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "control-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new NotificationHub(); var registry = new DeviceRegistry([], new(TimeSpan.FromSeconds(5))); await using var archive = new FileRunArchive(folder); var runs = new MeasurementRunService(registry, hub, [archive]);
            var plan = new ExperimentPlan("Control only", new SequenceStep(new("root"), [new AssignValueStep(new("value"), new("count"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))), new DelayStep(new("delay"), TimeSpan.FromMinutes(1))]));
            Assert.Empty(runs.PreviewPlan(plan).Devices); using var events = hub.Subscribe(); var running = runs.StartPlanAsync(plan, Token);
            try
            {
                Assert.Null((await Until(events, WorkbenchNotificationKind.RunStarted)).InstrumentId); runs.Pause(); Assert.Null((await Until(events, WorkbenchNotificationKind.RunPaused)).InstrumentId);
                runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!running.IsCompleted) { runs.Abort(); await running; } }
            Assert.Equal(MeasurementRunState.Aborted, runs.LatestDataSet!.State); Assert.Empty(runs.LatestDataSet.Instruments); Assert.Empty(runs.LatestDataSet.Rows);
            await using var reader = new FileRunArchive(folder); Assert.Empty((await reader.LoadAsync(runs.LatestDataSet.Id, Token)).DataSet.Instruments); Assert.False(runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public AxisProbe Field { get; } = new(MockEnvironmentCatalog.Field(id: new("magnet-1")));
        public AxisProbe Temperature { get; } = new(MockEnvironmentCatalog.Temperature(id: new("thermal-1")));
        public AxisProbe Angle { get; } = new(MockEnvironmentCatalog.Angle(id: new("rotation-1")));
        public IReadOnlyList<AxisProbe> Ports => [Field, Temperature, Angle]; public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? listener = null)
        {
            Registry = new(Ports.Select(p => (IDeviceSession)new AxisControllerSession(p, Hub)), Leases); Runs = new(Registry, Hub, listener is null ? null : [listener]);
        }
        public ExperimentPlan Plan(bool longDelay = false, bool twoReads = false)
        {
            ReadStep Read(string id) => new(new(id), Ports.Select(p => new ReadChannel(new(p.Definition.Device.Id.Value + ".actual"), new(p.Definition.Device.Id, p.Definition.Readback.Id))));
            var steps = new List<ExperimentStep> { new SetParameterStep(new("target"), new(Field.Definition.Device.Id, Field.Definition.Target.Id), new ConstantValue(new(1, PhysicalUnit.Tesla))), Read("first") };
            if (longDelay) steps.Add(new DelayStep(new("dwell"), TimeSpan.FromMinutes(1))); if (twoReads) steps.Add(Read("second"));
            return new("Independent axes", new SequenceStep(new("root"), steps));
        }
    }
    private sealed class AxisProbe(MockAxisController inner) : ITemperatureControlPort
    {
        public MockAxisController Inner { get; } = inner; public AxisControllerDefinition Definition => Inner.Definition;
        public int Connects { get; private set; }
        public int Holds { get; private set; }
        public bool FailNextSnapshot { get; set; }
        public Exception? HoldError { get; set; }
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) { Connects++; return Inner.ConnectAsync(token); }
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token)
        { if (FailNextSnapshot) { FailNextSnapshot = false; throw new IOException("field read failed"); } return Inner.GetSnapshotAsync(token); }
        public Task HoldAsync(CancellationToken token) { Holds++; return HoldError is { } error ? Task.FromException(error) : Inner.HoldAsync(token); }
        public Task SetTargetAsync(Quantity value, CancellationToken token) => Inner.SetTargetAsync(value, token);
        public Task SetRateAsync(Quantity value, CancellationToken token) => Inner.SetRateAsync(value, token);
        public Task DisconnectAsync(CancellationToken token) => Inner.DisconnectAsync(token);
        public Task PrepareSoftwareRampAsync(Quantity value, CancellationToken token) => Inner.PrepareSoftwareRampAsync(value, token);
        public Task SetHardwareRampAsync(Quantity value, CancellationToken token) => Inner.SetHardwareRampAsync(value, token);
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token) => Inner.ReadControlConfigurationAsync(token);
    }
    private sealed class Listener : IRunResultListener
    {
        public Action? FirstRow { get; set; }
        public bool BlockFinalization { get; init; }
        public bool FailFinalization { get; init; }
        public TaskCompletionSource Finalizing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int rows;
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { if (++rows == 1) FirstRow?.Invoke(); return Task.CompletedTask; }
        public async Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken token)
        { if (BlockFinalization) { Finalizing.TrySetResult(); await Release.Task; } if (FailFinalization) throw new IOException("result finalization failed"); }
    }
}

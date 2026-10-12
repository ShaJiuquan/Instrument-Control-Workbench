using InstrumentControlWorkbench.Application.Devices;
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

public sealed partial class Keithley6221ExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePureCurrentResistancePreservesModelAndIndependentQualityThroughArchive(bool routed)
    {
        var folder = Path.Combine(Path.GetTempPath(), "k6221-native-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive);
            var plan = b.Plan(routed); var document = new JsonExperimentPlanDocument(); await document.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            await b.Runs.StartPlanAsync(await document.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var e = Assert.Single(row.IndependentResistance);
            Assert.Equal(CurrentSourceModel.Keithley6221, e.SourceConfiguration.Model); Assert.Equal(100, e.SourceConfiguration.Compliance); Assert.Equal(100, e.Resistance.Value, 8);
            Assert.Equal(new(20e-6, PhysicalUnit.Ampere), e.SourceConfiguration.ManualRange); Assert.Equal(routed ? b.Route : null, e.Route);
            Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); Assert.All(row.Quality, q => Assert.Null(q.Status));
            Assert.Equal(Keithley6221Parameters.Current.Id, b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("I")).Source!.Parameter);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token);
            Assert.Equal(e, Assert.Single(Assert.Single(stored.DataSet.Rows).IndependentResistance)); await fresh.ExportCsvAsync(stored.DataSet.Id, Path.Combine(folder, "data.csv"), Token);
            Assert.Contains("Keithley6221", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("current")]
    [InlineData("range")]
    [InlineData("compliance")]
    [InlineData("output")]
    [InlineData("route")]
    public async Task DriftDuringIndependentReadDiscardsResistanceAndConfirmsSourceOff(string drift)
    {
        await using var b = new Bench(); b.Meter.OnRead = async token =>
        {
            var state = (await b.Source.GetSnapshotAsync(token)).Settings!;
            if (drift == "current") await b.Source.ConfigureAsync(new(new(2e-5, PhysicalUnit.Ampere), state.Compliance, state.ManualRange), token);
            if (drift == "compliance") await b.Source.ConfigureAsync(new(state.Current, new(50, PhysicalUnit.Volt), state.ManualRange), token);
            if (drift == "range") await b.Source.ConfigureAsync(new(state.Current, state.Compliance), token);
            if (drift == "output") await b.Source.SetOutputAsync(false, token);
            if (drift == "route") await b.Matrix.OpenAsync([b.Route.Closed[0]], token);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(true), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrDisconnectedSourceStopRetainsEveryClaimUntilExplicitRecovery(bool disconnected)
    {
        await using var b = new Bench(); b.Meter.OnRead = async token =>
        {
            if (disconnected) { await b.Source.SetOutputAsync(false, token); await b.Source.DisconnectAsync(token); }
            else b.Source.RefuseStop = true;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(true), Token)); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id, new(0, PhysicalUnit.Ampere), Token));
        b.Source.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseChecksPureSourceBeforeRestoringExcitationAndAbortStopsOutput(bool drift)
    {
        await using var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(false, new DelayStep(new("wait"), TimeSpan.FromSeconds(30))), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        var enables = b.Source.Enables; if (drift) await b.Source.ConfigureAsync(new(new(2e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt), new(20e-6, PhysicalUnit.Ampere)), Token);
        b.Runs.Resume();
        if (drift) { await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Equal(enables, b.Source.Enables); }
        else { await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await running; Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Source.LastStopTokenLive); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task CancelledExternalReadHasNoPartialResistanceAndIndependentCleanupStillRuns()
    {
        await using var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var run = b.Runs.StartPlanAsync(b.Plan(false), Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await run;
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Source.LastStopTokenLive); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("measured")]
    [InlineData("missingPreparation")]
    [InlineData("fractionalOutput")]
    [InlineData("nonnominalRange")]
    [InlineData("scanOverflow")]
    public async Task UnsupportedNativePolicyRejectsBeforeSourceConnection(string damage)
    {
        await using var b = new Bench(); var read = b.Read; ExperimentStep step = read;
        if (damage == "measured") step = new ReadCurrentResistanceStep(read.Id, read.Source, read.Meter, CurrentReference.MeasuredSourceReadback, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent);
        if (damage == "fractionalOutput") step = new SetParameterStep(new("bad"), new(read.Source, Keithley6221Parameters.Output.Id), new ConstantValue(new(0.5, PhysicalUnit.Dimensionless)));
        if (damage == "nonnominalRange") step = new SetParameterStep(new("bad"), new(read.Source, Keithley6221Parameters.ManualRange.Id), new ConstantValue(new(12e-6, PhysicalUnit.Ampere)));
        if (damage == "scanOverflow") step = new ScanStep(new("scan"), new(read.Source, Keithley6221Parameters.Current.Id), new("axis"), new([new(25e-6, PhysicalUnit.Ampere)]), TimeSpan.Zero, read);
        var plan = new ExperimentPlan("bad", step, currentSourcePreparations: damage == "missingPreparation" ? [] : [b.Preparation]);
        Assert.ThrowsAny<Exception>(() => b.Runs.PreviewPlan(plan)); Assert.Equal(0, b.Source.Connections); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ManualSettingsConfirmActualStateAndAreBlockedDuringRun()
    {
        await using var b = new Bench(); await b.Registry.ConnectAsync(b.Source.Descriptor.Id, Token);
        await b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Compliance.Id, new(105, PhysicalUnit.Volt), Token);
        Assert.Equal(new(105, PhysicalUnit.Volt), await b.Registry.ReadAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Compliance.Id, Token));
        await b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id, new(1e-5, PhysicalUnit.Ampere), Token);
        b.Source.RefuseSettings = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id, new(2e-5, PhysicalUnit.Ampere), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Output.Id, new(1, PhysicalUnit.Dimensionless), Token)); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        b.Source.RefuseSettings = false; await b.Registry.DisconnectAsync(b.Source.Descriptor.Id, Token); await b.Registry.ConnectAsync(b.Source.Descriptor.Id, Token);
        using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(false, new DelayStep(new("wait"), TimeSpan.FromSeconds(30))), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id, new(0, PhysicalUnit.Ampere), Token));
        b.Runs.Abort(); await running;
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    [Fact]
    public async Task PreparationRefusalNeverEnablesOutputAndDynamicRangeOverflowCleansUp()
    {
        await using var b = new Bench(); b.Source.RefuseSettings = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(false), Token)); Assert.Equal(0, b.Source.Enables); Assert.False(b.Runs.HasActiveRun);
        b.Source.RefuseSettings = false; var plan = new ExperimentPlan("Dynamic overflow", new SequenceStep(new("root"),
            [new SourceOutputStep(new("on"), b.Source.Descriptor.Id, true), new AssignValueStep(new("assign"), new("requested"), new ConstantValue(new(25e-6, PhysicalUnit.Ampere))),
             new SetParameterStep(new("set"), new(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id), new VariableValue(new("requested"))), b.Read]), currentSourcePreparations: [b.Preparation]);
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task TimedOutNonCooperativePureStopCannotReleaseOrOverlapRecovery()
    {
        await using var b = new Bench(); var owner = new RunId(Guid.NewGuid());
        var resources = await b.Registry.AcquireRunAsync([b.Source.Descriptor.Id, b.Meter.Descriptor.Id], owner, Token, TimeSpan.FromMilliseconds(40));
        await resources.ConnectAllAsync(Token); await resources.ConfigureCurrentSourceAsync(b.Source.Descriptor.Id, b.Preparation.Settings, Token); await resources.EnableSourceOutputAsync(b.Source.Descriptor.Id, Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Source.OnStop = async _ => { entered.TrySetResult(); await unblock.Task; }; b.Source.StopFinished = finished;
        try
        {
            var stop = resources.StopAllAsync(Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.NotEmpty(await stop);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token)); Assert.NotEmpty(await resources.StopAllAsync(Token, retryFailed: true)); Assert.Equal(1, b.Source.StopCalls);
            Assert.All(resources.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        }
        finally
        {
            b.Source.OnStop = null; unblock.TrySetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The expired stop token still makes the original task fail; retry only
            // after it is terminal, and retain ownership through fresh off proof.
            Assert.NotEmpty(await resources.StopAllAsync(Token));
            Assert.Empty(await resources.StopAllAsync(Token, retryFailed: true)); await resources.ReleaseAsync(Token);
        }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Equal(2, b.Source.StopCalls);
    }
    private sealed class Bench(IRunResultListener? listener = null, int columns = 4) : IAsyncDisposable
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public FaultSource Source { get; } = new(); public FaultMeter Meter { get; } = new();
        public MockMatrixSwitch Matrix { get; } = new(new(new(new("matrix"), "K3706", "Explicit matrix simulation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, columns)], [new("source")]));
        public MatrixRouteExpectation Route => new(Matrix.Definition.Device.Id, [new(1, 1, 1), new(1, 2, 2), new(1, 3, 1), new(1, 4, 2)]);
        private DeviceRegistry? registry; private MeasurementRunService? runs;
        public DeviceRegistry Registry => registry ??= new([new Keithley6221CurrentSourceSession(Source, Hub), new Keithley2182MeterSession(Meter, Hub), new MatrixSwitchSession(Matrix, Hub)], Leases);
        public MeasurementRunService Runs => runs ??= new(Registry, Hub, listener is null ? null : [listener]);
        public CurrentSourcePreparation Preparation => new(Source.Descriptor.Id, new(new(1e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt), new(20e-6, PhysicalUnit.Ampere)));
        public ReadCurrentResistanceStep Read => new(new("read"), Source.Descriptor.Id, Meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(2.220446049250313e-16, PhysicalUnit.Ampere), new ConstantValue(new(1e-5, PhysicalUnit.Ampere)));
        public ExperimentPlan Plan(bool routed, params ExperimentStep[] following)
        {
            var read = Read; if (routed) read = new(read.Id, read.Source, read.Meter, read.CurrentReference, read.VoltageColumn, read.CurrentColumn, read.ResistanceColumn, read.MinimumCurrent, read.ExpectedProgrammedCurrent, Route);
            return new("Pure source", new SequenceStep(new("root"), [.. routed ? new ExperimentStep[] { new RouteMatrixStep(new("route"), Route.Device, Route.Closed, TimeSpan.Zero) } : [], new SourceOutputStep(new("on"), Source.Descriptor.Id, true), read, .. following]), currentSourcePreparations: [Preparation]);
        }
        public async ValueTask DisposeAsync()
        {
            Source.RefuseStop = false; Source.RefuseSettings = false;
            if (runs?.HasActiveRun == true) await runs.RecoverSafetyAsync(Token, reconnect: true);
            if ((await Source.GetSnapshotAsync(Token)).Connected) { await Source.SafeStopAsync(Token); await Source.DisconnectAsync(Token); }
        }
    }
    private sealed class FaultSource : IK6221CurrentSourcePort
    {
        private readonly MockKeithley6221CurrentSource inner = new(new("source"), "Pure current simulation");
        public DriverKind Kind { get; set; } = DriverKind.Simulation;
        public DeviceDescriptor Descriptor => new(inner.Descriptor.Id, "K6221", "Current source test double", Kind, DeviceStopPolicy.OutputOff, Keithley6221Parameters.All);
        public Func<K6221CurrentSourceSnapshot, K6221CurrentSourceSnapshot>? Observe { get; set; }
        public bool RefuseStop { get; set; }
        public bool RefuseSettings { get; set; }
        public Func<Keithley6221Settings, Task>? OnConfigured { get; set; }
        public Func<CancellationToken, Task>? OnStop { get; set; }
        public TaskCompletionSource? StopFinished { get; set; }
        public int StopCalls { get; private set; }
        public bool LastStopTokenLive { get; private set; }
        public int Connections { get; private set; }
        public int Enables { get; private set; }
        public int Configurations { get; private set; }
        public int Disconnections { get; private set; }
        public async Task<K6221CurrentSourceSnapshot> ConnectAsync(CancellationToken token) { Connections++; var state = await inner.ConnectAsync(token); return Observe?.Invoke(state) ?? state; }
        public async Task<K6221CurrentSourceSnapshot> GetSnapshotAsync(CancellationToken token) { var state = await inner.GetSnapshotAsync(token); return Observe?.Invoke(state) ?? state; }
        public async Task ConfigureAsync(Keithley6221Settings settings, CancellationToken token) { token.ThrowIfCancellationRequested(); Configurations++; if (!RefuseSettings) await inner.ConfigureAsync(settings, token); if (OnConfigured is { } action) await action(settings); }
        public Task SetOutputAsync(bool enabled, CancellationToken token) { if (enabled) Enables++; return inner.SetOutputAsync(enabled, token); }
        public async Task SafeStopAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); LastStopTokenLive = true; StopCalls++;
            try { if (OnStop is { } action) await action(token); if (RefuseStop) throw new IOException("Output-off refused."); await inner.SafeStopAsync(token); }
            finally { StopFinished?.TrySetResult(); }
        }
        public Task DisconnectAsync(CancellationToken token) { Disconnections++; return inner.DisconnectAsync(token); }
    }
    private sealed class FaultMeter : IK2182MeterPort
    {
        private readonly MockKeithley2182Meter inner = new(new("meter"), "External voltage simulation");
        public DeviceDescriptor Descriptor => inner.Descriptor; public Func<CancellationToken, Task>? OnRead { get; set; }
        public Func<CancellationToken, Task>? OnAbort { get; set; }
        public Func<CancellationToken, Task>? OnSnapshot { get; set; }
        public int ReadCalls { get; private set; }
        public int AbortCalls { get; private set; }
        public Task<K2182MeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public async Task<K2182MeterSnapshot> GetSnapshotAsync(CancellationToken token) { if (OnSnapshot is { } action) await action(token); return await inner.GetSnapshotAsync(token); }
        public Task SelectChannelAsync(int channel, CancellationToken token) => inner.SelectChannelAsync(channel, token);
        public Task ConfigureAsync(Keithley2182Settings settings, CancellationToken token) => inner.ConfigureAsync(settings, token);
        public async Task<K2182VoltageReading> ReadAsync(CancellationToken token) { ReadCalls++; if (OnRead is { } action) await action(token); return await inner.ReadAsync(token); }
        public async Task AbortAcquisitionAsync(CancellationToken token) { AbortCalls++; if (OnAbort is { } action) await action(token); await inner.AbortAcquisitionAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
}

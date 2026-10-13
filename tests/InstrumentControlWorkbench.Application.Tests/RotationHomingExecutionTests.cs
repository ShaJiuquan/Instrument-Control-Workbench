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
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class RotationHomingExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData("zero_angle")]
    [InlineData("angle_zero")]
    [InlineData("rotator.zero")]
    public async Task ImportedAliasesUseDedicatedHomeOperationAndRequireCompletionBeforeRead(string alias)
    {
        var b = new Bench(); b.Axis.Complete = true; var plan = b.Import(alias); var timed = Assert.IsType<TimeoutStep>(plan.Root); var home = Assert.IsType<HomeRotationStep>(timed.Body);
        Assert.Equal(TimeSpan.FromSeconds(30), timed.Timeout); Assert.Equal(new(.5, PhysicalUnit.Degree), home.Tolerance); Assert.Equal(TimeSpan.FromMilliseconds(500), home.PollInterval);
        await b.Runs.StartPlanAsync(b.Plan(timed, b.Read()), Token); Assert.Equal(1, b.Axis.Homes); Assert.Equal(0, b.Axis.TargetWrites); Assert.Equal(0, Assert.Single(b.Runs.LatestDataSet!.Rows).Values[new("position")].Value); Assert.False(b.Runs.HasActiveRun); Assert.Equal(1, b.Axis.Holds);
    }
    [Fact]
    public void ConfiguredRotatorOptsIntoHomeWithoutAdvertisingItOnLegacyGenericAnglePorts()
    {
        Assert.False(MockEnvironmentCatalog.Angle().Definition.SupportsHoming);
        var configuration = new DeviceConfiguration(new("configured"), "ROTATOR", "Rotation simulator", ConfiguredDeviceCategory.Rotation, new SimulationDeviceConnection());
        var ports = new ConfiguredDeviceFactory().Create(configuration); Assert.True(ports.Axis!.Definition.SupportsHoming); Assert.IsAssignableFrom<IRotationHomingPort>(ports.Axis);
    }
    [Fact]
    public async Task NonHomingAxisRejectsAtPreflightWithoutConnection()
    {
        var b = new Bench(supportsHoming: false); Assert.Throws<NotSupportedException>(() => b.Runs.PreviewPlan(b.Plan(b.Home()))); Assert.Equal(0, b.Axis.Connections); Assert.Equal(0, b.Axis.Homes); await Task.CompletedTask;
    }
    [Fact]
    public void AdvertisedCapabilityCannotBeBackedByAnOrdinaryAxisPort()
    {
        var b = new Bench(); Assert.Throws<ArgumentException>(() => new AxisControllerSession(new OrdinaryPort(b.Axis), b.Hub));
    }
    [Theory]
    [InlineData("moving")]
    [InlineData("boundary")]
    [InlineData("target")]
    public async Task MovingBoundaryOrDriftedTargetCannotPublishFollowingRead(string damage)
    {
        var b = new Bench(); b.Axis.Complete = damage != "moving"; b.Axis.CompletionPosition = damage == "boundary" ? .5 : 0; b.Axis.WrongTarget = damage == "target";
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(new TimeoutStep(new("deadline"), TimeSpan.FromMilliseconds(40), b.Home()), b.Read()), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Axis.Holds); Assert.False(b.Axis.Moving); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task CancellationDuringHomeCommandDoesNotIssueFollowingReadAndStillHolds()
    {
        var b = new Bench(); b.Axis.WaitInStart = true; using var cancel = new CancellationTokenSource(); var running = b.Runs.StartPlanAsync(b.Plan(b.Home(), b.Read()), cancel.Token);
        await b.Axis.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Axis.Holds); Assert.False(b.Axis.Moving); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task DisconnectionDuringHomeWaitCannotPublishAnAngleReading()
    {
        var b = new Bench(); var running = b.Runs.StartPlanAsync(b.Plan(b.Home(), b.Read()), Token); await b.Axis.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Axis.DropConnection = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Axis.Holds); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task AbortWhileSeekingHoldsMotionAndReleasesOwnedResources()
    {
        var b = new Bench(); var running = b.Runs.StartPlanAsync(b.Plan(b.Home(), b.Read()), Token); await b.Axis.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Axis.Definition.Device.Id, b.Axis.Definition.Target.Id, new(5, PhysicalUnit.Degree), Token));
        b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Axis.Moving); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Axis.Definition.Device.Id, DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task PauseHoldsAndResumeReissuesDedicatedHomeInsteadOfNormalTargetZero()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Home(), b.Read()), Token); await b.Axis.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False(b.Axis.Moving); Assert.Equal(1, b.Axis.Homes); Assert.Equal(0, b.Axis.TargetWrites); b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); Assert.Equal(2, b.Axis.Homes); Assert.Equal(0, b.Axis.TargetWrites);
        b.Axis.Complete = true; await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FailedHoldRetainsResourcesUntilExplicitRecovery()
    {
        var b = new Bench(); b.Axis.RefuseHold = true; var running = b.Runs.StartPlanAsync(b.Plan(b.Home()), Token); await b.Axis.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Axis.Definition.Device.Id, DateTimeOffset.UtcNow)); b.Axis.RefuseHold = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Axis.Moving);
    }
    [Fact]
    public async Task SavedImportedHomeAndFreshArchivePreserveTheOperationAndAngleReading()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rotation-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive); b.Axis.Complete = true; var plan = b.Plan(b.Import("zero_angle").Root, b.Read()); var codec = new JsonExperimentPlanDocument(); var path = Path.Combine(folder, "home.json"); await codec.SaveAsync(path, plan, Token); var loaded = await codec.LoadAsync(path, Token); await b.Runs.StartPlanAsync(loaded, Token);
            var id = b.Runs.LatestDataSet!.Id; Assert.Equal(1, b.Axis.Homes); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(id, Token); Assert.Single(stored.DataSet.Rows); Assert.Contains("home_rotation", codec.Serialize(stored.DataSet.Plan!)); Assert.Equal(new(0, PhysicalUnit.Degree), stored.DataSet.Rows[0].Values[new("position")]); await fresh.ExportCsvAsync(id, Path.Combine(folder, "home.csv"), Token); Assert.Contains("position [Degree]", await File.ReadAllTextAsync(Path.Combine(folder, "home.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public void UnexpectedZeroCommandParametersRejectBeforeConnection()
    {
        var b = new Bench(); Assert.Throws<FormatException>(() => b.Import("zero_angle", new JsonObject { ["target_deg"] = 0 })); Assert.Equal(0, b.Axis.Connections);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private sealed class Bench
    {
        public RecordingRotation Axis { get; }
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? listener = null, bool supportsHoming = true) { Axis = new(supportsHoming); Registry = new([new AxisControllerSession(Axis, Hub)], Leases); Runs = new(Registry, Hub, listener is null ? null : [listener]); }
        public HomeRotationStep Home() => new(new("home"), Axis.Definition.Device.Id, new(.5, PhysicalUnit.Degree), TimeSpan.FromMilliseconds(1));
        public ReadStep Read() => new(new("read"), [new(new("position"), new(Axis.Definition.Device.Id, Axis.Definition.Readback.Id))]);
        public ExperimentPlan Plan(params ExperimentStep[] steps) => new("Rotation home", new SequenceStep(new("root"), steps));
        public ExperimentPlan Import(string alias, JsonObject? parameters = null) => new PpmsExperimentPlanImporter().Parse(new JsonObject { ["type"] = "command", ["name"] = alias, ["source_line"] = 7, ["params"] = parameters ?? new JsonObject() }.ToJsonString(), new ExperimentImportBindings([], [KeyValuePair.Create("angle", Axis.Definition)]));
    }
    private sealed class RecordingRotation : IRotationHomingPort
    {
        public RecordingRotation(bool supportsHoming)
        {
            var target = new ParameterDescriptor(new("angle.target"), "Target", PhysicalUnit.Degree, ParameterAccess.ReadWrite, new(-360, PhysicalUnit.Degree), new(360, PhysicalUnit.Degree)); var read = new ParameterDescriptor(new("angle.readback"), "Angle", PhysicalUnit.Degree, ParameterAccess.Read, new(-360, PhysicalUnit.Degree), new(360, PhysicalUnit.Degree)); var rate = new ParameterDescriptor(new("angle.rate"), "Rate", PhysicalUnit.DegreePerSecond, ParameterAccess.ReadWrite, new(.1, PhysicalUnit.DegreePerSecond), new(30, PhysicalUnit.DegreePerSecond));
            Definition = new(new(new("rotator"), "SIM-ANGLE", "Rotation simulator", DriverKind.Simulation, DeviceStopPolicy.HoldCurrentPosition, [target, read, rate]), target, read, rate, supportsHoming: supportsHoming);
        }
        public AxisControllerDefinition Definition { get; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Homes { get; private set; }
        public int Holds { get; private set; }
        public int TargetWrites { get; private set; }
        public int Connections { get; private set; }
        public bool WaitInStart { get; set; }
        public bool DropConnection { get; set; }
        public bool Complete { get; set; }
        public double CompletionPosition { get; set; }
        public bool WrongTarget { get; set; }
        public bool RefuseHold { get; set; }
        public bool Moving { get; private set; }
        private bool connected; private double position = 15; private double target = 15;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); connected = true; Connections++; return GetSnapshotAsync(token); }
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Moving && Complete) { position = CompletionPosition; Moving = false; } return Task.FromResult(new AxisControllerSnapshot(connected && !DropConnection, new(target, PhysicalUnit.Degree), new(position, PhysicalUnit.Degree), new(5, PhysicalUnit.DegreePerSecond), Moving, DateTimeOffset.UtcNow)); }
        public async Task StartHomingAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Homes++; target = WrongTarget ? 1 : 0; Moving = true; Started.TrySetResult(); if (WaitInStart) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        public Task SetTargetAsync(Quantity value, CancellationToken token) { token.ThrowIfCancellationRequested(); TargetWrites++; target = value.Value; Moving = target != position; return Task.CompletedTask; }
        public Task SetRateAsync(Quantity value, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task HoldAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Holds++; if (!RefuseHold) { Moving = false; target = position; } return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); connected = false; return Task.CompletedTask; }
    }
    private sealed class OrdinaryPort(RecordingRotation port) : IAxisControllerPort
    {
        public AxisControllerDefinition Definition => port.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => port.ConnectAsync(token); public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => port.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity value, CancellationToken token) => port.SetTargetAsync(value, token); public Task SetRateAsync(Quantity value, CancellationToken token) => port.SetRateAsync(value, token); public Task HoldAsync(CancellationToken token) => port.HoldAsync(token); public Task DisconnectAsync(CancellationToken token) => port.DisconnectAsync(token);
    }
}

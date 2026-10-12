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
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830ExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(Sr830InputMode.VoltageA)]
    [InlineData(Sr830InputMode.VoltageAB)]
    [InlineData(Sr830InputMode.Current1M)]
    [InlineData(Sr830InputMode.Current100M)]
    public async Task NativeFrameConfigurationAndArchiveRoundTripRetainRawValuesAndSettings(Sr830InputMode input)
    {
        var folder = Path.Combine(Path.GetTempPath(), "sr830-frame-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive); var plan = b.Plan(b.Write("mode", Sr830Parameters.InputMode, (int)input), b.Write("time", Sr830Parameters.TimeConstant, .3), b.Frame("first", input), b.Write("sensitivity", Sr830Parameters.Sensitivity, .005), b.Frame("second", input));
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); var loadedPlan = await codec.LoadAsync(Path.Combine(folder, "plan.json"), Token); await b.Runs.StartPlanAsync(loadedPlan, Token);
            var data = b.Runs.LatestDataSet!; Assert.Equal(2, b.Meter.Reads); Assert.Equal(2, data.Rows.Count); Assert.Equal(new[] { .01, .005 }, data.Rows.Select((row, i) => row.Values[new((i == 0 ? "first" : "second") + ".sensitivity_v")].Value));
            Assert.All(data.Rows, row => { Assert.Equal(4, row.Quality.Count); Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); });
            Assert.Equal(.0023, data.Rows[0].Values[new("first.r_v")].Value); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.Equal(1, b.Meter.Stops); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Meter.Descriptor.Id, DateTimeOffset.UtcNow));
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.SelectMany(r => r.Values.OrderBy(v => v.Key.Value, StringComparer.Ordinal)), stored.DataSet.Rows.SelectMany(r => r.Values.OrderBy(v => v.Key.Value, StringComparer.Ordinal)));
            Assert.NotNull(stored.DataSet.Blackboard); await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "raw.csv"), Token); Assert.Contains("first.time_constant_s", await File.ReadAllTextAsync(Path.Combine(folder, "raw.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialScalarAndFullFrameCannotReuseColumnsInEitherOrder(bool scalarFirst)
    {
        var b = new Bench(); var scalar = new ReadStep(new("scalar"), [new(new("read.x_v"), new(b.Meter.Descriptor.Id, Sr830Parameters.X.Id))]);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(scalarFirst ? b.Plan(scalar, b.Frame()) : b.Plan(b.Frame(), scalar))); Assert.Equal(0, b.Meter.Connections);
    }
    [Fact]
    public void TwoFrameStepsCannotShareAPrefixWithDifferentInputInterpretations()
    {
        var b = new Bench(); var other = new ReadSr830FrameStep(new("other"), b.Meter.Descriptor.Id, Sr830InputMode.Current1M, new("read"));
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Plan(b.Frame(), other))); Assert.Equal(0, b.Meter.Connections);
    }
    [Theory]
    [InlineData("sensitivity", .015)]
    [InlineData("time", .2)]
    [InlineData("slope", 7)]
    [InlineData("input", 1.5)]
    public async Task InvalidDiscreteWritesFailPreflightWithoutConnection(string kind, double value)
    {
        var b = new Bench(); var parameter = kind switch { "sensitivity" => Sr830Parameters.Sensitivity, "time" => Sr830Parameters.TimeConstant, "slope" => Sr830Parameters.FilterSlope, _ => Sr830Parameters.InputMode };
        Assert.ThrowsAny<ArgumentException>(() => b.Runs.PreviewPlan(b.Plan(b.Write("bad", parameter, value), b.Frame()))); Assert.Equal(0, b.Meter.Connections); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("settings")]
    [InlineData("old-frame")]
    [InlineData("future-frame")]
    public async Task InputDriftAndOutOfBoundaryTimestampsPublishNoMeasuredValues(string fault)
    {
        var b = new Bench(); b.Meter.OnRead = _ => { if (fault == "settings") b.Meter.Settings = new(Sr830InputMode.VoltageA, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12); else b.Meter.FrameTime = DateTimeOffset.UtcNow.AddMinutes(fault == "old-frame" ? -1 : 1); return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Frame()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("read.x_v"))); Assert.Equal(1, b.Meter.Stops); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task CancellationDuringSnapDiscardsFrameAndConfirmsPassiveStop()
    {
        var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        using var cancel = new CancellationTokenSource(); var run = b.Runs.StartPlanAsync(b.Plan(b.Frame()), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("read.x_v"))); Assert.Equal(1, b.Meter.Stops); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task IgnoredInputWriteDoesNotProceedToAcquisition()
    {
        var b = new Bench(); b.Meter.IgnoreWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("time", Sr830Parameters.TimeConstant, .3), b.Frame()), Token)); Assert.Equal(0, b.Meter.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FrozenInputMismatchRejectsBeforeReading()
    {
        var b = new Bench(); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Frame(input: Sr830InputMode.Current1M)), Token)); Assert.Equal(0, b.Meter.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }
    [Fact]
    public async Task StopFailureRetainsOwnershipUntilExplicitRecovery()
    {
        var b = new Bench(); b.Meter.RefuseStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Frame()), Token)); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Meter.Descriptor.Id, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Meter.Descriptor.Id, Sr830Parameters.TimeConstant.Id, new(.3, PhysicalUnit.Second), Token));
        b.Meter.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Meter.Descriptor.Id, DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseKeepsInputOwnershipAndRejectsConfigurationDrift(bool drift)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var run = b.Runs.StartPlanAsync(b.Plan(b.Frame(), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), b.Frame("later")), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Meter.Descriptor.Id, Sr830Parameters.TimeConstant.Id, new(.3, PhysicalUnit.Second), Token));
        if (drift) b.Meter.Settings = new(Sr830InputMode.VoltageAB, new(.005, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12);
        b.Runs.Resume();
        if (drift) await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        else { await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task NewFrameInvalidatesEarlierPassiveStopProof()
    {
        var b = new Bench(); var resources = await b.Registry.AcquireRunAsync([b.Meter.Descriptor.Id], RunId.New(), Token); await resources.ConnectAllAsync(Token); Assert.Empty(await resources.StopAllAsync(Token));
        await resources.ReadSr830FrameAsync(b.Meter.Descriptor.Id, Sr830InputMode.VoltageAB, Token); await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token)); await resources.ReleaseAsync(Token); Assert.Equal(2, b.Meter.Stops);
    }
    [Theory]
    [InlineData("input")]
    [InlineData("fractional-input")]
    [InlineData("sensitivity")]
    [InlineData("partial-frame")]
    public async Task ArchiveValidationRejectsIncompleteOrInconsistentLockInInputEvidence(string fault)
    {
        var b = new Bench(); var plan = b.Plan(b.Frame()); await b.Runs.StartPlanAsync(plan, Token); var original = Assert.Single(b.Runs.LatestDataSet!.Rows); var values = new Dictionary<ParameterId, Quantity>(original.Values);
        if (fault == "partial-frame") values.Remove(new("read.r_v"));
        else if (fault == "sensitivity") values[new("read.sensitivity_v")] = new(.015, PhysicalUnit.Volt);
        else values[new("read.input_mode")] = new(fault == "input" ? 2 : 1.5, PhysicalUnit.Dimensionless);
        Assert.ThrowsAny<ArgumentException>(() => new RunDataRowValidator(plan, b.Runs.LatestDataSet.Schema).Validate(new(original.Timestamp, values, quality: original.Quality)));
    }
    [Fact]
    public async Task WideMeasurementGroupPreservesEachCompleteRawFrame()
    {
        var b = new Bench(); var group = new MeasurementGroupStep(new("point"), [new("First", b.Frame()), new("Second", b.Frame("second"))]); await b.Runs.StartPlanAsync(b.Plan(group), Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); Assert.NotNull(rows[2].MeasurementGroup); Assert.Equal(8, rows[2].Quality.Count); Assert.Equal(.0023, rows[2].Values[new("second.r_v")].Value);
    }
    private static async Task Until(NotificationSubscription subscription, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await subscription.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public RecordingMeter Meter { get; }
        public MockKeithley2450SourceMeter Source { get; } = new(); public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? listener = null, bool reference = false) { Meter = new(reference); Registry = new([new Sr830MeterSession(Meter, Hub), new InstrumentManager(Source, Hub)], Leases); Runs = new(Registry, Hub, listener is null ? null : [listener]); }
        public SetParameterStep Write(string id, ParameterDescriptor p, double value) => new(new(id), new(Meter.Descriptor.Id, p.Id), new ConstantValue(new(value, p.Unit)));
        public ReadSr830FrameStep Frame(string id = "read", Sr830InputMode input = Sr830InputMode.VoltageAB) => new(new(id), Meter.Descriptor.Id, input, new(id));
        public ExperimentPlan Plan(params ExperimentStep[] steps) => new("Passive SR830", new SequenceStep(new("root"), steps));
    }
    private sealed class RecordingMeter : ISr830MeterPort
    {
        public RecordingMeter(bool reference = false)
        {
            Descriptor = new(new("lockin"), "SR830", "Lock-in simulator", DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, reference ? Sr830Parameters.WithReference : Sr830Parameters.All);
            Reference = reference ? new(Sr830ReferenceSource.Internal, new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt), 1) : null;
        }
        public DeviceDescriptor Descriptor { get; }
        public Sr830ReferenceReadback? Reference { get; set; }
        public Sr830InputSettings Settings { get; set; } = new(Sr830InputMode.VoltageAB, new(.01, PhysicalUnit.Volt), new(.1, PhysicalUnit.Second), 12);
        public int Reads { get; private set; }
        public int Stops { get; private set; }
        public int Connections { get; private set; }
        public bool IgnoreWrites { get; set; }
        public bool RefuseStop { get; set; }
        public DateTimeOffset? FrameTime { get; set; }
        public Func<CancellationToken, Task>? OnRead { get; set; }
        private bool connected; private bool acquiring;
        public Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); connected = true; Connections++; return GetSnapshotAsync(token); }
        public Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new Sr830MeterSnapshot(connected, acquiring, connected ? Settings : null, DateTimeOffset.UtcNow, connected ? Reference : null)); }
        public Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token) { token.ThrowIfCancellationRequested(); if (!IgnoreWrites) Settings = settings; return Task.CompletedTask; }
        public async Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token) { Reads++; if (OnRead is not null) await OnRead(token); token.ThrowIfCancellationRequested(); return new(new(.001, PhysicalUnit.Volt), new(-.002, PhysicalUnit.Volt), new(.0023, PhysicalUnit.Volt), new(-63, PhysicalUnit.Degree), FrameTime ?? DateTimeOffset.UtcNow); }
        public Task AbortAcquisitionAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Stops++; acquiring = RefuseStop; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); connected = false; return Task.CompletedTask; }
    }
}

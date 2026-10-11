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
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class StandaloneMeterTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(1, AveragingMode.Repeat)]
    [InlineData(1, AveragingMode.Moving)]
    [InlineData(2, AveragingMode.Repeat)]
    [InlineData(2, AveragingMode.Moving)]
    public async Task PassiveConfigurationAndTwoDeviceOwnershipSurviveNativePlanArchiveAndCsv(int channel, AveragingMode mode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "standalone-meter-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(listener: archive);
            var plan = b.Plan(b.Write("input", Keithley2182Parameters.Channel, channel), b.Write("range", Keithley2182Parameters.ManualRange, 1),
                b.Write("nplc", Keithley2182Parameters.Nplc, 2), b.Write("filter_count", Keithley2182Parameters.FilterCount, 3),
                b.Write("mode", Keithley2182Parameters.FilterMode, (int)mode), b.Write("window", Keithley2182Parameters.FilterWindowPercent, 0.5),
                b.Write("filter", Keithley2182Parameters.FilterEnabled, 1), b.Read("read1"), b.Write("next_nplc", Keithley2182Parameters.Nplc, 4), b.Read("read2"));
            var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            await b.Runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var data = b.Runs.LatestDataSet!; Assert.Equal(2, data.Instruments.Count); Assert.Equal(2, data.Rows.Count);
            Assert.Equal(new[] { 2.0, 4.0 }, data.Rows.Select(r => Assert.Single(r.Quality).MeterConfiguration!.Nplc));
            Assert.All(data.Rows, r => { var q = Assert.Single(r.Quality); Assert.Equal(channel, q.MeterConfiguration!.Channel); Assert.Equal(mode, q.MeterConfiguration.FilterMode); Assert.Equal(ReadingQualityState.Unobserved, r.QualityState); Assert.Null(q.Status); Assert.Null(q.AcquisitionConfiguration); });
            Assert.Equal(new(1, PhysicalUnit.Dimensionless), Assert.Single(data.Instruments.Single(i => i.InstrumentId == b.Meter.Descriptor.Id).Parameters, p => p.Descriptor.Id == Keithley2182Parameters.Nplc.Id).Value);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Equal(1, b.Meter.Stops);
            Assert.False(b.Leases.IsLeased(b.Meter.Descriptor.Id, DateTimeOffset.UtcNow)); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token);
            Assert.Equal(data.Rows.SelectMany(r => r.Quality), loaded.DataSet.Rows.SelectMany(r => r.Quality));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); Assert.Contains("meterConfiguration", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task WideMeasurementPointRetainsBothInputConfigurationsAsOriginalEvidence()
    {
        var b = new Bench(); var group = new MeasurementGroupStep(new("point"),
            [new("Input", b.Read("first")), new("Input", new SequenceStep(new("second_member"), [b.Write("input", Keithley2182Parameters.Channel, 2), b.Read("second")]))]);
        await b.Runs.StartPlanAsync(b.Plan(group), Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count);
        Assert.NotNull(rows[2].MeasurementGroup); Assert.Equal(rows.Take(2).SelectMany(r => r.Quality), rows[2].Quality);
        Assert.Equal(new[] { 1, 2 }, rows[2].Quality.Select(q => q.MeterConfiguration!.Channel)); Assert.Equal(ReadingQualityState.Unobserved, rows[2].QualityState);
    }
    [Fact]
    public async Task DriftDuringMeterReadDiscardsFrameAndStopsTheExcitation()
    {
        var b = new Bench(); b.Meter.OnRead = token => { b.Meter.Settings = new(2, null, 1, false, 10, AveragingMode.Repeat, 0.1); return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task RefusedMeterStopRetainsWholeGroupUntilExplicitRecoveryEvenAfterSourceOff()
    {
        var b = new Bench(); b.Meter.RefuseStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Meter.Descriptor.Id, Keithley2182Parameters.Voltage.Id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Session.WriteParameterAsync(Keithley2182Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless), Token));
        b.Meter.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun); Assert.Equal(2, b.Meter.Stops);
    }
    [Fact]
    public async Task AbortedMeterIoStillUsesIndependentStopTokenAndDisablesSource()
    {
        var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnRead = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read()), Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await running;
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Meter.LastStopTokenWasLive); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseHoldsMeterAndSourceOwnershipAndConfirmsMeterBeforeSourceRestoration(bool drift)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read("first"), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), b.Read("second")), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Meter.Descriptor.Id, Keithley2182Parameters.Channel.Id, new(2, PhysicalUnit.Dimensionless), Token));
        if (drift) b.Meter.Settings = new(2, null, 1, false, 10, AveragingMode.Repeat, 0.1);
        b.Runs.Resume();
        if (drift) { await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Single(b.Runs.LatestDataSet!.Rows); }
        else { await Until(events, WorkbenchNotificationKind.RunResumed); Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); b.Runs.Abort(); await running; }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task EarlierIdleProofIsInvalidatedByNewAcquisitionBeforeResourceRelease()
    {
        var b = new Bench(); var resources = await b.Registry.AcquireRunAsync([b.Meter.Descriptor.Id], RunId.New(), Token);
        await resources.ConnectAllAsync(Token); Assert.Empty(await resources.StopAllAsync(Token)); Assert.Equal(1, b.Meter.Stops);
        await resources.ReadObservationAsync(b.Meter.Descriptor.Id, Keithley2182Parameters.Voltage.Id, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token)); Assert.Equal(2, b.Meter.Stops); await resources.ReleaseAsync(Token);
    }
    [Fact]
    public async Task IgnoredWriteAndInvalidInputSpanCannotProduceRowsOrUnsafeExcitation()
    {
        var b = new Bench(); b.Meter.IgnoreWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("set", Keithley2182Parameters.Nplc, 3), b.Read()), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        b.Meter.IgnoreWrites = false; await b.Session.ConnectDeviceAsync(Token); await b.Session.WriteParameterAsync(Keithley2182Parameters.Channel.Id, new(2, PhysicalUnit.Dimensionless), Token); var writes = b.Meter.Writes;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Session.WriteParameterAsync(Keithley2182Parameters.ManualRange.Id, new(0.01, PhysicalUnit.Volt), Token)); Assert.Equal(writes, b.Meter.Writes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FractionalControlsCannotReachAdapterForLiteralOrDynamicValues(bool dynamic)
    {
        var b = new Bench(); var value = new Quantity(2.5, PhysicalUnit.Dimensionless);
        var set = new SetParameterStep(new("invalid"), new(b.Meter.Descriptor.Id, Keithley2182Parameters.FilterCount.Id), dynamic ? new VariableValue(new("count")) : new ConstantValue(value));
        var steps = dynamic ? new ExperimentStep[] { new AssignValueStep(new("assign"), new("count"), new ConstantValue(value)), set, b.Read() } : [set, b.Read()];
        await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Runs.StartPlanAsync(b.Plan(steps), Token)); Assert.Equal(0, b.Meter.Writes);
        if (!dynamic) Assert.Equal(0, b.Meter.Connections); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task DisconnectedPhysicalMeterHasNoSettingsAndCannotReleaseItsStopObligation()
    {
        var b = new Bench(hardwareMeter: true); var initial = await b.Session.GetDeviceStatusAsync(Token);
        Assert.Empty(initial.Values); Assert.False(initial.SafetyConfirmed);
        b.Meter.OnRead = token => b.Meter.DisconnectAsync(token);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Runs.HasActiveRun);
        b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("unit")]
    [InlineData("overflow")]
    [InlineData("oldTimestamp")]
    public async Task WrongUnitInputOverflowAndStaleReadingCannotEnterDataset(string failure)
    {
        var b = new Bench();
        if (failure == "unit") b.Meter.ReadingOverride = new(0.001, PhysicalUnit.Ampere);
        if (failure == "overflow") b.Meter.ReadingOverride = new(13, PhysicalUnit.Volt);
        if (failure == "oldTimestamp") b.Meter.TimestampOverride = DateTimeOffset.UtcNow.AddHours(-1);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("input", Keithley2182Parameters.Channel, 2), b.Read()), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public RecordingMeter Meter { get; }
        public MockKeithley2450SourceMeter Source { get; } = new(new("source"), "Simulation source");
        public Keithley2182MeterSession Session { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? listener = null, bool hardwareMeter = false)
        {
            Meter = new(hardwareMeter); Session = new(Meter, Hub); Registry = new([Session, new InstrumentManager(Source, Hub)], Leases); Runs = new(Registry, Hub, listener is null ? null : [listener]);
        }
        public SetParameterStep Write(string id, ParameterDescriptor parameter, double value) => new(new(id), new(Meter.Descriptor.Id, parameter.Id), new ConstantValue(new(value, parameter.Unit)));
        public ReadStep Read(string id = "read") => new(new(id), [new(new(id + ".voltage"), new(Meter.Descriptor.Id, Keithley2182Parameters.Voltage.Id))]);
        public ExperimentPlan Plan(params ExperimentStep[] steps) => new("Independent source and meter", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), .. steps]),
            [new(Source.Id, SourceFunction.Current, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.TwoWire, 1, true))]);
    }
    private sealed class RecordingMeter : IK2182MeterPort
    {
        public RecordingMeter(bool hardware = false) => Descriptor = new(new("meter"), "K2182", "Recording passive meter", hardware ? DriverKind.Hardware : DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Keithley2182Parameters.All);
        public DeviceDescriptor Descriptor { get; }
        public Quantity? ReadingOverride { get; set; }
        public DateTimeOffset? TimestampOverride { get; set; }
        public Keithley2182Settings Settings { get; set; } = new(1, null, 1, false, 10, AveragingMode.Repeat, 0.1);
        public Func<CancellationToken, Task>? OnRead { get; set; }
        public bool RefuseStop { get; set; }
        public bool IgnoreWrites { get; set; }
        public bool LastStopTokenWasLive { get; private set; }
        public int Stops { get; private set; }
        public int Writes { get; private set; }
        public int Connections { get; private set; }
        private bool connected; private bool acquiring;
        public Task<K2182MeterSnapshot> ConnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); connected = true; Connections++; return GetSnapshotAsync(token); }
        public Task<K2182MeterSnapshot> GetSnapshotAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new K2182MeterSnapshot(connected, acquiring, connected ? Settings : null, DateTimeOffset.UtcNow)); }
        public Task ConfigureAsync(Keithley2182Settings settings, CancellationToken token) { token.ThrowIfCancellationRequested(); Writes++; if (!IgnoreWrites) Settings = settings; return Task.CompletedTask; }
        public async Task<K2182VoltageReading> ReadAsync(CancellationToken token)
        {
            acquiring = true;
            try { if (OnRead is { } read) await read(token); token.ThrowIfCancellationRequested(); return new(ReadingOverride ?? new(Settings.Channel * 0.001, PhysicalUnit.Volt), TimestampOverride ?? DateTimeOffset.UtcNow); }
            finally { acquiring = false; }
        }
        public Task AbortAcquisitionAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Stops++; LastStopTokenWasLive = !token.IsCancellationRequested; if (RefuseStop) throw new IOException("Idle could not be confirmed."); acquiring = false; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); connected = false; acquiring = false; return Task.CompletedTask; }
    }
}

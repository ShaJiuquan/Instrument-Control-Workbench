using System.Text.Json.Nodes;
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

public sealed class PicoammeterTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public Meter Port { get; } = new();
        public Keithley6485MeterSession Session { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null)
        {
            Session = new(Port, Hub); Registry = new([Session], Leases); Runs = new(Registry, Hub, archive is null ? null : [archive]);
        }
        public SetParameterStep Write(string id, ParameterDescriptor p, double value) => new(new(id), new(Port.Descriptor.Id, p.Id), new ConstantValue(new(value, p.Unit)));
        public ReadStep Read(string id = "read") => new(new(id), [new(new(id + ".i"), new(Port.Descriptor.Id, Keithley6485Parameters.Current.Id))]);
        public ExperimentPlan Plan(params ExperimentStep[] steps) => new("Passive current", new SequenceStep(new("root"), steps));
    }
    private sealed class Meter : IK6485MeterPort
    {
        public DeviceDescriptor Descriptor { get; } = new(new("meter"), "K6485", "Recording picoammeter", DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Keithley6485Parameters.All);
        public Keithley6485Settings Settings { get; set; } = new(null, 1, false, false, false, 10, AveragingMode.Repeat);
        public Func<CancellationToken, Task>? OnRead { get; set; }
        public bool IgnoreWrites { get; set; }
        public bool RefuseStop { get; set; }
        public bool StopTokenLive { get; private set; }
        public int Stops { get; private set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Quantity Reading { get; set; } = new(1e-9, PhysicalUnit.Ampere);
        public DateTimeOffset? CapturedAt { get; set; }
        private bool connected, acquiring;
        public Task<K6485MeterSnapshot> ConnectAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); connected = true; return GetSnapshotAsync(t); }
        public Task<K6485MeterSnapshot> GetSnapshotAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); return Task.FromResult(new K6485MeterSnapshot(connected, acquiring, connected ? Settings : null, DateTimeOffset.UtcNow)); }
        public Task ConfigureAsync(Keithley6485Settings s, CancellationToken t) { t.ThrowIfCancellationRequested(); Writes++; if (!IgnoreWrites) Settings = s; return Task.CompletedTask; }
        public async Task<K6485CurrentReading> ReadAsync(CancellationToken t)
        {
            Reads++; acquiring = true;
            try { if (OnRead is { } read) await read(t); t.ThrowIfCancellationRequested(); return new(Reading, CapturedAt ?? DateTimeOffset.UtcNow); }
            finally { acquiring = false; }
        }
        public Task AbortAcquisitionAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); Stops++; StopTokenLive = !t.IsCancellationRequested; if (RefuseStop) throw new IOException("Cannot confirm idle."); acquiring = false; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); connected = false; acquiring = false; return Task.CompletedTask; }
    }
    [Fact]
    public async Task ZeroCheckRefusesSampleReadWithoutInvokingAcquisition()
    {
        var b = new Bench(); b.Port.Settings = b.Port.Settings.WithParameter(Keithley6485Parameters.ZeroCheck.Id, new(1, PhysicalUnit.Dimensionless));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token));
        Assert.Equal(0, b.Port.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        await b.Runs.StartPlanAsync(b.Plan(b.Write("zero_off", Keithley6485Parameters.ZeroCheck, 0), b.Read()), Token); Assert.Single(b.Runs.LatestDataSet.Rows);
    }
    [Theory]
    [InlineData("drift")]
    [InlineData("disconnect")]
    [InlineData("unit")]
    [InlineData("overflow")]
    [InlineData("manual_overflow")]
    [InlineData("stale")]
    public async Task InvalidFramesNeverReachData(string failure)
    {
        var b = new Bench();
        if (failure == "drift") b.Port.OnRead = _ => { b.Port.Settings = b.Port.Settings.WithParameter(Keithley6485Parameters.ZeroCheck.Id, new(1, PhysicalUnit.Dimensionless)); return Task.CompletedTask; };
        if (failure == "disconnect") b.Port.OnRead = b.Port.DisconnectAsync;
        if (failure == "unit") b.Port.Reading = new(1e-9, PhysicalUnit.Volt);
        if (failure == "overflow") b.Port.Reading = new(0.022, PhysicalUnit.Ampere);
        if (failure == "manual_overflow") { b.Port.Settings = b.Port.Settings.WithParameter(Keithley6485Parameters.ManualRange.Id, new(2e-9, PhysicalUnit.Ampere)); b.Port.Reading = new(2.2e-9, PhysicalUnit.Ampere); }
        if (failure == "stale") b.Port.CapturedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task UnconfirmedWriteCannotReachAcquisition()
    {
        var b = new Bench(); b.Port.IgnoreWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("integration", Keithley6485Parameters.Nplc, 3), b.Read()), Token)); Assert.Equal(0, b.Port.Reads);
    }
    [Fact]
    public async Task StopFailureRetainsLeaseUntilExplicitRecovery()
    {
        var b = new Bench(); b.Port.RefuseStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Port.Descriptor.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Port.Descriptor.Id, Keithley6485Parameters.Current.Id, Token));
        b.Port.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task NewAcquisitionInvalidatesEarlierStopProof()
    {
        var b = new Bench(); var r = await b.Registry.AcquireRunAsync([b.Port.Descriptor.Id], RunId.New(), Token); await r.ConnectAllAsync(Token); Assert.Empty(await r.StopAllAsync(Token));
        await r.ReadObservationAsync(b.Port.Descriptor.Id, Keithley6485Parameters.Current.Id, Token); await Assert.ThrowsAsync<InvalidOperationException>(() => r.ReleaseAsync(Token));
        Assert.Empty(await r.StopAllAsync(Token)); await r.ReleaseAsync(Token); Assert.Equal(2, b.Port.Stops);
    }
    [Fact]
    public async Task AbortUsesLiveCleanupToken()
    {
        var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Port.OnRead = async t => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, t); };
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read()), Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await running;
        Assert.True(b.Port.StopTokenLive); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseRejectsConfigurationDriftAndRetainsOwnership(bool drift)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read(), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), b.Read("second")), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Session.WriteParameterAsync(Keithley6485Parameters.ZeroCheck.Id, new(1, PhysicalUnit.Dimensionless), Token));
        if (drift) b.Port.Settings = b.Port.Settings.WithParameter(Keithley6485Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless));
        b.Runs.Resume();
        if (drift) { await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Single(b.Runs.LatestDataSet!.Rows); }
        else { await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await running; }
        Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FractionalNativeWritesRejectWithoutAdapterMutation(bool dynamic)
    {
        var b = new Bench(); var value = new Quantity(2.5, PhysicalUnit.Dimensionless);
        var write = new SetParameterStep(new("bad"), new(b.Port.Descriptor.Id, Keithley6485Parameters.AveragingCount.Id), dynamic ? new VariableValue(new("count")) : new ConstantValue(value));
        var steps = dynamic ? new ExperimentStep[] { new AssignValueStep(new("assign"), new("count"), new ConstantValue(value)), write, b.Read() } : [write, b.Read()];
        await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Runs.StartPlanAsync(b.Plan(steps), Token)); Assert.Equal(0, b.Port.Writes); Assert.Equal(0, b.Port.Reads);
        if (!dynamic) Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("manual_overflow")]
    public async Task ArchiveProvenanceRejectsMissingForeignOrOverrangeConfiguration(string failure)
    {
        var b = new Bench(); var plan = b.Plan(b.Read()); await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; var raw = Assert.Single(data.Rows);
        var configuration = failure == "manual_overflow" ? new Keithley6485Settings(new(2e-9, PhysicalUnit.Ampere), 1, false, false, false, 10, AveragingMode.Repeat) : b.Port.Settings;
        var quality = failure == "missing" ? Array.Empty<AcquisitionQuality>() : [new AcquisitionQuality(new("read"), b.Port.Descriptor.Id, new("read.i"), raw.Timestamp, null, picoammeterConfiguration: configuration)];
        var forged = new MeasurementDataRow(raw.Timestamp, failure == "manual_overflow" ? new Dictionary<ParameterId, Quantity> { [new("read.i")] = new(3e-9, PhysicalUnit.Ampere) } : raw.Values, quality: quality);
        var targetPlan = failure == "foreign" ? b.Plan(new ReadStep(new("read"), [new(new("read.i"), new(b.Port.Descriptor.Id, Keithley6485Parameters.Nplc.Id))])) : plan;
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(targetPlan, data.Schema).Validate(forged));
    }
    [Fact]
    public async Task WidePointPreservesBothConfigurations()
    {
        var b = new Bench(); var group = new MeasurementGroupStep(new("point"), [new("Current", b.Read("first")), new("Current", new SequenceStep(new("next"), [b.Write("change", Keithley6485Parameters.Nplc, 2), b.Read("second")]))]);
        await b.Runs.StartPlanAsync(b.Plan(group), Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); Assert.NotNull(rows[2].MeasurementGroup);
        Assert.Equal(new[] { 1.0, 2.0 }, rows[2].Quality.Select(q => q.PicoammeterConfiguration!.Nplc)); Assert.Equal(rows.Take(2).SelectMany(r => r.Quality), rows[2].Quality);
    }
    private static async Task Until(NotificationSubscription s, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await s.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private static JsonObject Command(string name, JsonObject p) => new() { ["type"] = "command", ["name"] = name, ["source_line"] = 23, ["params"] = p };
    private static JsonObject Measurement(JsonObject p) => new() { ["type"] = "measurement", ["name"] = "measure_current", ["params"] = p };
    private static ExperimentImportBindings Bindings() => new(new Dictionary<string, InstrumentId> { ["k6485"] = new("meter") }, [], standaloneCurrentMeters: [new("meter")]);
    [Theory]
    [InlineData("k6485.set_nplc", "{\"nplc\":61}")]
    [InlineData("k6485.set_range", "{\"range\":3e-9}")]
    [InlineData("k6485.set_range", "{\"range_a\":2e-9}")]
    [InlineData("k6485.set_auto_range", "{\"enabled\":false}")]
    [InlineData("k6485.set_auto_range", "{\"enabled\":true,\"range_a\":2e-9}")]
    [InlineData("k6485.set_auto_range", "{\"enabled\":true,\"auto_range\":false}")]
    [InlineData("k6485.set_zero_check", "{}")]
    [InlineData("k6485.set_zero_check", "{\"enabled\":null}")]
    [InlineData("k6485.set_zero_correct", "{\"enabled\":0.5}")]
    [InlineData("k6485.set_averaging", "{\"count\":2.5}")]
    [InlineData("k6485.set_averaging", "{\"mode\":\"moving\"}")]
    [InlineData("k6485.set_averaging", "{}")]
    public void MalformedCommandsRejectWithSourceLocation(string name, string p)
    {
        var error = Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(Command(name, JsonNode.Parse(p)!.AsObject()).ToJsonString(), Bindings())); Assert.Contains("source line 23", error.Message);
    }
    [Theory]
    [InlineData("channel", "\"ch1\"")]
    [InlineData("meter_range_a", "3e-9")]
    [InlineData("meter_range_v", "1")]
    [InlineData("filter_window_percent", "1")]
    [InlineData("sensing_mode", "\"4wire\"")]
    [InlineData("sense_channel", "2")]
    public void PassiveMeasurementRejectsUnsupportedControls(string field, string value)
    {
        var p = new JsonObject { ["instrument"] = "k6485", ["channel"] = "none" }; p[field] = JsonNode.Parse(value);
        Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(Measurement(p).ToJsonString(), Bindings()));
    }
    [Theory]
    [InlineData("visa")]
    [InlineData("serial")]
    [InlineData("tcp")]
    public void UnsupportedPhysicalCatalogNeverFallsBackToSimulation(string transport)
    {
        DeviceConnection connection = transport switch { "visa" => new VisaDeviceConnection("GPIB0::7::INSTR"), "serial" => new SerialDeviceConnection("test-port", TimeSpan.FromSeconds(2)), _ => new TcpDeviceConnection("127.0.0.1", 12345, "\n", TimeSpan.FromSeconds(2)) };
        Assert.Throws<NotSupportedException>(() => new ConfiguredDeviceRuntime(new(null, [new(new("meter"), "K6485", "Physical", ConfiguredDeviceCategory.Meter, connection)], new Dictionary<string, InstrumentId> { ["k6485"] = new("meter") }), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()));
    }
    [Fact]
    public async Task ConfiguredImportNativeSaveFreshArchiveAndCsvPreserveObservedSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "picoammeter-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var hub = new NotificationHub();
            var catalog = new DeviceConfigurationCatalog(null, [new(new("meter"), "K6485", "Simulated picoammeter", ConfiguredDeviceCategory.Meter, new SimulationDeviceConnection())], new Dictionary<string, InstrumentId> { ["k6485"] = new("meter") });
            var devices = new JsonDeviceConfigurationDocument(); var runtime = new ConfiguredDeviceRuntime(devices.Parse(devices.Serialize(catalog)), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
            var nodes = new JsonArray(Command("k6485.set_range", new() { ["range"] = 2e-8 }), Command("k6485.set_nplc", new() { ["nplc"] = 2 }), Command("k6485.set_zero_check", new() { ["enabled"] = false }), Command("k6485.set_zero_correct", new() { ["enabled"] = true }), Command("k6485.set_averaging", new() { ["enabled"] = true, ["count"] = 3, ["mode"] = "mov" }));
            var plan = new PpmsExperimentPlanImporter().Parse(new JsonObject { ["type"] = "sequence", ["children"] = nodes }.ToJsonString(), runtime.CreateImportBindings());
            plan = new("Configured picoammeter", new SequenceStep(new("native_root"), [plan.Root, new ReadStep(new("direct"), [new(new("i"), new(new("meter"), Keithley6485Parameters.Current.Id))])]), metadata: plan.Metadata);
            var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            var runs = new MeasurementRunService(runtime.Registry, hub, [archive]); await runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var data = runs.LatestDataSet!; var row = Assert.Single(data.Rows); var q = Assert.Single(row.Quality);
            Assert.Equal(new(1e-9, PhysicalUnit.Ampere), row.Values[new("i")]); Assert.Equal(new Keithley6485Settings(new(2e-8, PhysicalUnit.Ampere), 2, false, true, true, 3, AveragingMode.Moving), q.PicoammeterConfiguration);
            Assert.Null(q.Status); Assert.Null(q.MeterConfiguration); Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); Assert.False(runs.HasActiveRun);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token); Assert.Equal(row.Quality, Assert.Single(loaded.DataSet.Rows).Quality);
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "out.csv"), Token); Assert.Contains("picoammeterConfiguration", await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")));
            var extension = new PpmsExperimentPlanImporter().Parse(Measurement(new() { ["instrument"] = "k6485", ["channel"] = "none", ["nplc"] = 3, ["meter_range_a"] = 2e-9 }).ToJsonString(), runtime.CreateImportBindings());
            await runs.StartPlanAsync(extension, Token); Assert.Equal(3, Assert.Single(Assert.Single(runs.LatestDataSet!.Rows).Quality).PicoammeterConfiguration!.Nplc);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

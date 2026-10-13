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

public sealed class ElectrometerTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public Meter Port { get; } = new();
        public Keithley6514MeterSession Session { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null)
        {
            Session = new(Port, Hub); Registry = new([Session], Leases); Runs = new(Registry, Hub, archive is null ? null : [archive]);
        }
        public SetParameterStep Write(string id, ParameterDescriptor p, double value) => new(new(id), new(Port.Descriptor.Id, p.Id), new ConstantValue(new(value, p.Unit)));
        public ReadStep Read(string id = "read", ElectrometerFunction function = ElectrometerFunction.Current) => new(new(id), [new(new(id + ".value"), new(Port.Descriptor.Id, Keithley6514Parameters.Reading(function).Id))]);
        public ExperimentPlan Plan(params ExperimentStep[] steps) => new("Passive current", new SequenceStep(new("root"), steps));
    }
    private sealed class Meter : IK6514MeterPort
    {
        public Meter(bool hardware = false) => Descriptor = new(new("meter"), "K6514", "Recording electrometer", hardware ? DriverKind.Hardware : DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Keithley6514Parameters.All);
        public DeviceDescriptor Descriptor { get; }
        public Keithley6514Settings Settings { get; set; } = Initial().WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(0, PhysicalUnit.Dimensionless));
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
        public Task<K6514MeterSnapshot> ConnectAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); connected = true; return GetSnapshotAsync(t); }
        public Task<K6514MeterSnapshot> GetSnapshotAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); return Task.FromResult(new K6514MeterSnapshot(connected, acquiring, connected ? Settings : null, DateTimeOffset.UtcNow)); }
        public bool IgnoreSelection { get; set; }
        public bool DriftOnSelection { get; set; }
        public ElectrometerFunction? ReturnedFunction { get; set; }
        public int Selections { get; private set; }
        public Task SelectFunctionAsync(ElectrometerFunction function, CancellationToken t)
        {
            t.ThrowIfCancellationRequested(); Selections++;
            if (!IgnoreSelection) Settings = Settings.SelectFunction(function);
            if (DriftOnSelection) Settings = Settings.WithParameter(Keithley6514Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless));
            return Task.CompletedTask;
        }
        public Task ConfigureAsync(Keithley6514Settings s, CancellationToken t) { t.ThrowIfCancellationRequested(); Writes++; if (!IgnoreWrites) Settings = s; return Task.CompletedTask; }
        public async Task<K6514Reading> ReadAsync(CancellationToken t)
        {
            Reads++; acquiring = true;
            try { if (OnRead is { } read) await read(t); t.ThrowIfCancellationRequested(); return new(ReturnedFunction ?? Settings.Function, Reading, CapturedAt ?? DateTimeOffset.UtcNow); }
            finally { acquiring = false; }
        }
        public Task AbortAcquisitionAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); Stops++; StopTokenLive = !t.IsCancellationRequested; if (RefuseStop) throw new IOException("Cannot confirm idle."); acquiring = false; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken t) { t.ThrowIfCancellationRequested(); connected = false; acquiring = false; return Task.CompletedTask; }
    }
    [Fact]
    public async Task ZeroCheckRefusesSampleReadWithoutInvokingAcquisition()
    {
        var b = new Bench(); b.Port.Settings = b.Port.Settings.WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(1, PhysicalUnit.Dimensionless));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token));
        Assert.Equal(0, b.Port.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
        await b.Runs.StartPlanAsync(b.Plan(b.Write("zero_off", Keithley6514Parameters.ZeroCheck, 0), b.Read()), Token); Assert.Single(b.Runs.LatestDataSet.Rows);
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
        if (failure == "drift") b.Port.OnRead = _ => { b.Port.Settings = b.Port.Settings.WithParameter(Keithley6514Parameters.ZeroCheck.Id, new(1, PhysicalUnit.Dimensionless)); return Task.CompletedTask; };
        if (failure == "disconnect") b.Port.OnRead = b.Port.DisconnectAsync;
        if (failure == "unit") b.Port.Reading = new(1e-9, PhysicalUnit.Volt);
        if (failure == "overflow") b.Port.Reading = new(0.022, PhysicalUnit.Ampere);
        if (failure == "manual_overflow") { b.Port.Settings = b.Port.Settings.WithParameter(Keithley6514Parameters.Range(ElectrometerFunction.Current).Id, new(2e-9, PhysicalUnit.Ampere)); b.Port.Reading = new(2.2e-9, PhysicalUnit.Ampere); }
        if (failure == "stale") b.Port.CapturedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task UnconfirmedWriteCannotReachAcquisition()
    {
        var b = new Bench(); b.Port.IgnoreWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("integration", Keithley6514Parameters.Nplc, 3), b.Read()), Token)); Assert.Equal(0, b.Port.Reads);
    }
    [Fact]
    public async Task StopFailureRetainsLeaseUntilExplicitRecovery()
    {
        var b = new Bench(); b.Port.RefuseStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Port.Descriptor.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.ReadAsync(b.Port.Descriptor.Id, Keithley6514Parameters.Reading(ElectrometerFunction.Current).Id, Token));
        b.Port.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task NewAcquisitionInvalidatesEarlierStopProof()
    {
        var b = new Bench(); var r = await b.Registry.AcquireRunAsync([b.Port.Descriptor.Id], RunId.New(), Token); await r.ConnectAllAsync(Token); Assert.Empty(await r.StopAllAsync(Token));
        await r.ReadObservationAsync(b.Port.Descriptor.Id, Keithley6514Parameters.Reading(ElectrometerFunction.Current).Id, Token); await Assert.ThrowsAsync<InvalidOperationException>(() => r.ReleaseAsync(Token));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Session.WriteParameterAsync(Keithley6514Parameters.ZeroCheck.Id, new(1, PhysicalUnit.Dimensionless), Token));
        if (drift) b.Port.Settings = b.Port.Settings.WithParameter(Keithley6514Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless));
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
        var write = new SetParameterStep(new("bad"), new(b.Port.Descriptor.Id, Keithley6514Parameters.AveragingCount.Id), dynamic ? new VariableValue(new("count")) : new ConstantValue(value));
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
        var configuration = failure == "manual_overflow" ? b.Port.Settings.WithParameter(Keithley6514Parameters.Range(ElectrometerFunction.Current).Id, new(2e-9, PhysicalUnit.Ampere)) : b.Port.Settings;
        var quality = failure == "missing" ? Array.Empty<AcquisitionQuality>() : [new AcquisitionQuality(new("read"), b.Port.Descriptor.Id, new("read.value"), raw.Timestamp, null, electrometerConfiguration: configuration)];
        var forged = new MeasurementDataRow(raw.Timestamp, failure == "manual_overflow" ? new Dictionary<ParameterId, Quantity> { [new("read.value")] = new(3e-9, PhysicalUnit.Ampere) } : raw.Values, quality: quality);
        var targetPlan = failure == "foreign" ? b.Plan(new ReadStep(new("read"), [new(new("read.value"), new(b.Port.Descriptor.Id, Keithley6514Parameters.Nplc.Id))])) : plan;
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(targetPlan, data.Schema).Validate(forged));
    }
    [Fact]
    public async Task WidePointPreservesBothConfigurations()
    {
        var b = new Bench(); var group = new MeasurementGroupStep(new("point"), [new("Current", b.Read("first")), new("Current", new SequenceStep(new("next"), [b.Write("change", Keithley6514Parameters.Nplc, 2), b.Read("second")]))]);
        await b.Runs.StartPlanAsync(b.Plan(group), Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); Assert.NotNull(rows[2].MeasurementGroup);
        Assert.Equal(new[] { 1.0, 2.0 }, rows[2].Quality.Select(q => q.ElectrometerConfiguration!.Nplc)); Assert.Equal(rows.Take(2).SelectMany(r => r.Quality), rows[2].Quality);
    }
    private static async Task Until(NotificationSubscription s, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await s.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private static JsonObject Command(string name, JsonObject p) => new() { ["type"] = "command", ["name"] = name, ["source_line"] = 23, ["params"] = p };
    private static JsonObject Measurement(JsonObject p) => new() { ["type"] = "measurement", ["name"] = "measure_current", ["params"] = p };
    private static ExperimentImportBindings Bindings() => new(new Dictionary<string, InstrumentId> { ["k6514"] = new("meter") }, [], electrometers: [new("meter")]);
    [Theory]
    [InlineData("k6514.set_function", "{\"function\":\"invalid\"}")]
    [InlineData("k6514.set_nplc", "{\"function\":\"charge\",\"nplc\":11}")]
    [InlineData("k6514.set_range", "{\"function\":\"charge\",\"range\":-1}")]
    [InlineData("k6514.set_range", "{\"function\":\"res\",\"range\":3e11}")]
    [InlineData("k6514.set_auto_range", "{\"function\":\"current\"}")]
    [InlineData("k6514.set_auto_range", "{\"function\":\"current\",\"enabled\":true,\"auto_range\":false}")]
    [InlineData("k6514.set_zero_check", "{}")]
    [InlineData("k6514.set_zero_check", "{\"enabled\":null}")]
    [InlineData("k6514.set_zero_correct", "{\"enabled\":0.5}")]
    [InlineData("k6514.set_averaging", "{\"count\":2.5}")]
    [InlineData("k6514.set_averaging", "{\"count\":101}")]
    [InlineData("k6514.set_averaging", "{\"mode\":\"moving\"}")]
    [InlineData("k6514.set_averaging", "{}")]
    public void MalformedCommandsRejectWithSourceLocation(string name, string p)
    {
        var error = Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(Command(name, JsonNode.Parse(p)!.AsObject()).ToJsonString(), Bindings())); Assert.Contains("source line 23", error.Message);
    }
    [Theory]
    [InlineData("channel", "\"ch1\"")]
    [InlineData("meter_range_a", "0.022")]
    [InlineData("meter_range_v", "1")]
    [InlineData("filter_window_percent", "1")]
    [InlineData("sensing_mode", "\"4wire\"")]
    [InlineData("sense_channel", "2")]
    public void PassiveMeasurementRejectsUnsupportedControls(string field, string value)
    {
        var p = new JsonObject { ["instrument"] = "k6514", ["channel"] = "none" }; p[field] = JsonNode.Parse(value);
        Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(Measurement(p).ToJsonString(), Bindings()));
    }
    [Theory]
    [InlineData("visa")]
    [InlineData("serial")]
    [InlineData("tcp")]
    public void UnsupportedPhysicalCatalogNeverFallsBackToSimulation(string transport)
    {
        DeviceConnection connection = transport switch { "visa" => new VisaDeviceConnection("GPIB0::7::INSTR"), "serial" => new SerialDeviceConnection("test-port", TimeSpan.FromSeconds(2)), _ => new TcpDeviceConnection("127.0.0.1", 12345, "\n", TimeSpan.FromSeconds(2)) };
        Assert.Throws<NotSupportedException>(() => new ConfiguredDeviceRuntime(new(null, [new(new("meter"), "K6514", "Physical", ConfiguredDeviceCategory.Meter, connection)], new Dictionary<string, InstrumentId> { ["k6514"] = new("meter") }), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()));
    }
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public async Task MaximumDepthElectrometerReadRemainsBoundedAndMetadataOnly(int depth)
    {
        var b = new Bench(); ExperimentStep root = b.Read();
        for (var index = 0; index < depth; index++) root = new SequenceStep(new("nested" + index), [root]);
        var preview = b.Runs.PreviewPlan(new("Deep electrometer", root)); Assert.Equal(depth + 1, preview.MaximumStepExecutions);
        Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected); Assert.Equal(0, b.Port.Reads);
    }
    [Fact]
    public void HardwareCannotClaimAnIdleAcquisitionAsOhmsExcitationStopProof()
        => Assert.Throws<NotSupportedException>(() => new Keithley6514MeterSession(new Meter(hardware: true), new()));
    [Fact]
    public async Task MixedFunctionReadRejectsBeforeConnection()
    {
        var b = new Bench(); var read = new ReadStep(new("mixed"), [new(new("i"), new(b.Port.Descriptor.Id, Keithley6514Parameters.Reading(ElectrometerFunction.Current).Id)), new(new("q"), new(b.Port.Descriptor.Id, Keithley6514Parameters.Reading(ElectrometerFunction.Charge).Id))]);
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(b.Plan(read))); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected); Assert.Equal(0, b.Port.Reads);
    }
    [Theory]
    [InlineData("current")]
    [InlineData("voltage")]
    [InlineData("resistance")]
    [InlineData("charge")]
    public async Task ModeSpecificRangeAndSharedNplcCommandsDoNotSwitchTheActiveMode(string function)
    {
        var b = new Bench(); var f = Enum.Parse<ElectrometerFunction>(function, true);
        var nodes = new JsonArray(Command("k6514.set_range", new() { ["function"] = function, ["range"] = Keithley6514Settings.Ranges(f)[0] }), Command("k6514.set_nplc", new() { ["function"] = function, ["nplc"] = 3 }), Command("k6514.set_auto_range", new() { ["function"] = function, ["auto_range"] = true }), Command("k6514.set_auto_range", new() { ["function"] = function, ["enabled"] = false }));
        var plan = new PpmsExperimentPlanImporter().Parse(new JsonObject { ["type"] = "sequence", ["children"] = nodes }.ToJsonString(), Bindings()); await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(ElectrometerFunction.Current, b.Port.Settings.Function); Assert.Equal(3, b.Port.Settings.Nplc); Assert.False(b.Port.Settings.Range(f).AutoRange); Assert.Equal(Keithley6514Settings.Ranges(f)[0], b.Port.Settings.Range(f).Span.Value); Assert.Equal(0, b.Port.Selections);
    }
    private static Keithley6514Settings Initial() => new(ElectrometerFunction.Current,
        new(new(2e-4, PhysicalUnit.Ampere), true), new(new(20, PhysicalUnit.Volt), true), new(new(2e5, PhysicalUnit.Ohm), true), new(new(2e-6, PhysicalUnit.Coulomb), true), 1, true, false, false, 10, AveragingMode.Repeat);
    [Theory]
    [InlineData("charge")]
    [InlineData("resistance")]
    [InlineData("voltage")]
    public async Task FunctionChangesRequireExplicitZeroCheck(string function)
    {
        var b = new Bench(); var f = Enum.Parse<ElectrometerFunction>(function, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("select", Keithley6514Parameters.Function, (int)f)), Token)); Assert.Equal(0, b.Port.Selections);
        await b.Runs.StartPlanAsync(b.Plan(b.Write("zero", Keithley6514Parameters.ZeroCheck, 1), b.Write("select", Keithley6514Parameters.Function, (int)f)), Token);
        Assert.Equal(f, b.Port.Settings.Function); Assert.True(b.Port.Settings.ZeroCheck);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedSelectionOrChangedUnrequestedRegistersReject(bool drift)
    {
        var b = new Bench(); b.Port.IgnoreSelection = !drift; b.Port.DriftOnSelection = drift;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("zero", Keithley6514Parameters.ZeroCheck, 1), b.Write("select", Keithley6514Parameters.Function, 3), b.Write("zero_off", Keithley6514Parameters.ZeroCheck, 0), b.Read("charge", ElectrometerFunction.Charge)), Token)); Assert.Equal(0, b.Port.Reads);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongRequestedOrReturnedFunctionDiscardsAcquisition(bool returned)
    {
        var b = new Bench(); b.Port.ReturnedFunction = returned ? ElectrometerFunction.Voltage : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read("bad", returned ? ElectrometerFunction.Current : ElectrometerFunction.Charge)), Token)); Assert.Equal(returned ? 1 : 0, b.Port.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }
    [Fact]
    public async Task AllModesImportSaveFreshArchiveAndCsvPreserveIndependentSpansAndSharedRegisters()
    {
        var folder = Path.Combine(Path.GetTempPath(), "electrometer-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var hub = new NotificationHub();
            var catalog = new DeviceConfigurationCatalog(null, [new(new("meter"), "K6514", "Simulated electrometer", ConfiguredDeviceCategory.Meter, new SimulationDeviceConnection())], new Dictionary<string, InstrumentId> { ["k6514"] = new("meter") });
            var devices = new JsonDeviceConfigurationDocument(); var runtime = new ConfiguredDeviceRuntime(devices.Parse(devices.Serialize(catalog)), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
            var imported = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-k6514-import.json"), runtime.CreateImportBindings(), Token);
            var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), imported, Token);
            var runs = new MeasurementRunService(runtime.Registry, hub, [archive]); await runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var data = runs.LatestDataSet!; Assert.Equal(4, data.Rows.Count); Assert.False(runs.HasActiveRun);
            Assert.Equal(Enum.GetValues<ElectrometerFunction>(), data.Rows.Select(r => Assert.Single(r.Quality).ElectrometerConfiguration!.Function));
            Assert.Equal(new[] { PhysicalUnit.Ampere, PhysicalUnit.Volt, PhysicalUnit.Ohm, PhysicalUnit.Coulomb }, data.Rows.Select(r => Assert.Single(r.Values, p => p.Value.Unit != PhysicalUnit.Second).Value.Unit));
            Assert.All(data.Rows, r => { var q = Assert.Single(r.Quality); Assert.Null(q.Status); Assert.Null(q.MeterConfiguration); Assert.Null(q.PicoammeterConfiguration); Assert.False(q.ElectrometerConfiguration!.ZeroCheck); Assert.Equal(ReadingQualityState.Unobserved, r.QualityState); });
            var last = Assert.Single(data.Rows[^1].Quality).ElectrometerConfiguration!;
            Assert.Equal(2, last.Nplc); Assert.Equal(3, last.AveragingCount); Assert.Equal(AveragingMode.Repeat, last.AveragingMode); Assert.True(last.AveragingEnabled);
            Assert.Equal(new(2e-11, PhysicalUnit.Ampere), last.Current.Span); Assert.Equal(new(2, PhysicalUnit.Volt), last.Voltage.Span); Assert.Equal(new(2e3, PhysicalUnit.Ohm), last.Resistance.Span); Assert.Equal(new(2e-8, PhysicalUnit.Coulomb), last.Charge.Span);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.SelectMany(r => r.Quality), loaded.DataSet.Rows.SelectMany(r => r.Quality));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "out.csv"), Token); Assert.Contains("electrometerConfiguration", await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")));
            var otherHub = new NotificationHub(); var replay = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), otherHub); var otherRun = new MeasurementRunService(replay.Registry, otherHub); await otherRun.StartPlanAsync(loaded.DataSet.Plan!, Token);
            Assert.Equal(data.Rows.SelectMany(r => r.Values.Values.Where(v => v.Unit != PhysicalUnit.Second)), otherRun.LatestDataSet!.Rows.SelectMany(r => r.Values.Values.Where(v => v.Unit != PhysicalUnit.Second)));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

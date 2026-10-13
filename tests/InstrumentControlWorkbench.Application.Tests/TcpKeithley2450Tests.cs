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
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TcpKeithley2450Tests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly string Pair = ":MEAS? \"defbuffer1\", SOUR, READ, SOURSTAT, STAT";
    [Fact]
    public async Task PublishedTcpTemplateIsExplicitlyDisabledAndCreatesNoLiveSource()
    {
        var started = await new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new())
            .StartAsync(Path.Combine(AppContext.BaseDirectory, "devices", "k2450-tcp-template.json"), null, Token);
        Assert.Null(started.Document.Catalog.PrimarySource); Assert.False(Assert.Single(started.Document.Catalog.Devices).Enabled);
        Assert.Empty(started.Devices.Registry.Devices); Assert.Empty(started.Devices.Sources); Assert.False(started.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedTcpConfigurationStartsOfflineThenExecutesAndReopensTheActualArchive(bool primarySelected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tcp-startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); var id = new InstrumentId("tcp-smu");
            var catalog = new DeviceConfigurationCatalog(primarySelected ? id : null,
                [new(id, "K2450", "Saved TCP source", ConfiguredDeviceCategory.SourceMeter, peer.Server.Configuration())], new Dictionary<string, InstrumentId> { ["k2450"] = id });
            var path = Path.Combine(folder, "devices.json"); var doc = new JsonDeviceConfigurationDocument(); await doc.SaveAsync(path, catalog, Token);
            var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); await using var archive = new FileRunArchive(Path.Combine(folder, "runs"));
            var started = await new DeviceConfigurationStartup(doc, new ConfiguredDeviceFactory(), leases, new()).StartAsync(path, null, Token, [archive]);
            Assert.Equal(DeviceConfigurationOrigin.SavedFile, started.Origin); Assert.Equal(0, peer.Server.Connections);
            var plan = new ExperimentPlan("Saved TCP acquisition", new SequenceStep(new("root"),
                [new SourceOutputStep(new("on"), id, true), new ReadSourceSenseStep(new("read"), id, SourceFunction.Voltage, new("voltage"), new("current"))]),
                [new(id, SourceFunction.Voltage, new(0.02, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
            var plans = new JsonExperimentPlanDocument(); await plans.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            await started.Runs.StartPlanAsync(await plans.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            Assert.False(peer.Output); Assert.False(started.Runs.HasActiveRun); Assert.False(leases.IsLeased(id, DateTimeOffset.UtcNow));
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(started.Runs.LatestDataSet!.Id, Token);
            Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State); Assert.Equal(peer.ActualSource, Assert.Single(saved.DataSet.Rows).Values[new("voltage")].Value);
            var instrument = Assert.Single(saved.DataSet.Instruments); Assert.Equal(peer.Identifier, instrument.Identifier); Assert.Equal("Saved TCP source", instrument.Configuration["display_name"]);
            Assert.Equal("Hardware", instrument.Configuration["driver_kind"]); await started.Devices.Registry.DisconnectAsync(id, Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ChangingLiveSourceFunctionRejectsBeforeAnyMutatingCommand()
    {
        await using var peer = new Keithley2450ScpiPeer(); var driver = new TcpKeithley2450SourceMeter(new("tcp"), "Source", peer.Server.Configuration());
        await driver.ConnectAsync(Token); await driver.EnableOutputAsync(Token); var before = peer.Server.Commands.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConfigureSourceAsync(new(SourceFunction.Current, 0.02, 21), Token));
        Assert.All(peer.Server.Commands.Skip(before), c => Assert.EndsWith("?", c)); Assert.True(peer.Output);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.DisconnectAsync(Token)); Assert.True(peer.Output);
        await driver.SafeStopAsync(Token); await driver.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(":FORM:DATA?", "REAL")]
    [InlineData(":SENS:COUN?", "2")]
    [InlineData(":SOUR:VOLT:READ:BACK?", "0")]
    public async Task UnconfirmedAcquisitionConfigurationCannotProduceRows(string query, string reply)
    {
        await using var peer = new Keithley2450ScpiPeer(); peer.Override = (command, _) => command == query ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(reply)) : null;
        var b = new Bench(peer); await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.DoesNotContain(Pair, peer.Server.Commands); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
        await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task DeviceErrorAfterValidAcquisitionPreservesTheErrorAndDiscardsTheRow()
    {
        await using var peer = new Keithley2450ScpiPeer(); peer.Override = (command, _) => { if (command == Pair) peer.Errors.Enqueue("-200,\"Acquisition failed\""); return null; };
        var b = new Bench(peer); var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(), Token));
        Assert.Contains("-200", error.Message); Assert.Contains("Acquisition failed", error.Message); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task NativeDocumentAndFactoryConstructHardwareWithoutSocketIoOrFabricatedInitialParameters()
    {
        await using var peer = new Keithley2450ScpiPeer(); var config = new DeviceConfiguration(new("tcp-smu"), "K2450", "TCP source", ConfiguredDeviceCategory.SourceMeter, peer.Server.Configuration());
        var original = new DeviceConfigurationCatalog(config.Id, [config], new Dictionary<string, InstrumentId> { ["k2450"] = config.Id });
        var doc = new JsonDeviceConfigurationDocument(); var catalog = doc.Parse(doc.Serialize(original)); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), leases, new());
        Assert.Equal(0, peer.Server.Connections); var status = await runtime.Registry.GetStatusAsync(config.Id, Token);
        Assert.Equal(DriverKind.Hardware, status.Descriptor.DriverKind); Assert.False(status.Connected); Assert.False(status.SafetyConfirmed); Assert.Empty(status.Values);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Sources[config.Id].CaptureParametersAsync(Token));
        Assert.Equal(0, peer.Server.Connections); Assert.False(leases.IsLeased(config.Id, DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, SenseFunction.Current, PhysicalUnit.Volt, PhysicalUnit.Ampere)]
    [InlineData(SourceFunction.Current, SenseFunction.Voltage, PhysicalUnit.Ampere, PhysicalUnit.Volt)]
    public async Task BothSourceFunctionsReconcileConfigureAndAcquireOneActualPairThenStop(SourceFunction source, SenseFunction sense, PhysicalUnit sourceUnit, PhysicalUnit senseUnit)
    {
        await using var peer = new Keithley2450ScpiPeer(); var driver = new TcpKeithley2450SourceMeter(new("tcp"), "Source", peer.Server.Configuration());
        var initial = await driver.ConnectAsync(Token); Assert.Equal(peer.Identifier, initial.Identifier); Assert.Equal(Terminals.Rear, initial.Terminals); Assert.False(initial.OutputEnabled);
        await driver.ConfigureSourceAsync(new(source, 0.02, source == SourceFunction.Voltage ? 105e-6 : 21), Token);
        await driver.ConfigureSenseAsync(new(sense, SensingMode.FourWire, 0.5, false), Token); await driver.EnableOutputAsync(Token);
        var reading = await driver.ReadSourceSenseAsync(Token); Assert.Equal(peer.ActualSource, reading.Source.Value); Assert.NotEqual(0.02, reading.Source.Value);
        Assert.Equal(peer.ActualSense, reading.Sense.Value); Assert.Equal(sourceUnit, reading.Source.Unit); Assert.Equal(senseUnit, reading.Sense.Unit);
        Assert.Contains(source == SourceFunction.Voltage ? ":SOUR:VOLT:READ:BACK ON" : ":SOUR:CURR:READ:BACK ON", peer.Server.Commands);
        Assert.DoesNotContain(peer.Server.Commands, c => c.Contains("*RST", StringComparison.Ordinal) || c.StartsWith("*LANG ", StringComparison.Ordinal));
        await driver.SafeStopAsync(Token); Assert.False(peer.Output); await driver.DisconnectAsync(Token); Assert.False((await driver.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("wrong model")]
    [InlineData("wrong manufacturer")]
    [InlineData("incomplete identity")]
    [InlineData("SCPI2400")]
    [InlineData("TSP")]
    public async Task WrongIdentityAndLanguageRejectWithoutChangingHardware(string reason)
    {
        await using var peer = new Keithley2450ScpiPeer { Output = true };
        if (reason == "wrong model") peer.Identifier = "KEITHLEY,MODEL 2400,TEST,1";
        else if (reason == "wrong manufacturer") peer.Identifier = "NOT KEITHLEY,MODEL 2450,TEST,1";
        else if (reason == "incomplete identity") peer.Identifier = "KEITHLEY,MODEL 2450";
        else peer.Language = reason;
        var driver = new TcpKeithley2450SourceMeter(new("tcp"), "Source", peer.Server.Configuration());
        await Assert.ThrowsAsync<NotSupportedException>(() => driver.ConnectAsync(Token)); Assert.False((await driver.GetSnapshotAsync(Token)).Connected); Assert.True(peer.Output);
        Assert.All(peer.Server.Commands, c => Assert.EndsWith("?", c));
    }
    [Fact]
    public async Task InvalidRequestsAndOffStateAcquisitionCannotEnableOutput()
    {
        await using var peer = new Keithley2450ScpiPeer(); var driver = new TcpKeithley2450SourceMeter(new("tcp"), "Source", peer.Server.Configuration());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.ConfigureSourceAsync(new(SourceFunction.Voltage, 100, 1), Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.ConfigureSenseAsync(new(SenseFunction.Current, SensingMode.TwoWire, double.NaN, true), Token)); Assert.Equal(0, peer.Server.Connections);
        await driver.ConnectAsync(Token); await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ReadAsync(Token)); await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ReadSourceSenseAsync(Token));
        Assert.DoesNotContain(peer.Server.Commands, c => c.StartsWith(":MEAS?", StringComparison.Ordinal) || c == ":OUTP ON"); Assert.False(peer.Output); await driver.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task CoupledQuadrantWritesUseAValidIntermediateConfiguration(SourceFunction function)
    {
        await using var peer = new Keithley2450ScpiPeer(); var driver = new TcpKeithley2450SourceMeter(new("tcp"), "Source", peer.Server.Configuration()); await driver.ConnectAsync(Token);
        var high = function == SourceFunction.Voltage ? new SourceSetpoint(function, 100, 0.1) : new SourceSetpoint(function, 0.5, 20);
        var low = function == SourceFunction.Voltage ? new SourceSetpoint(function, 1, 0.5) : new SourceSetpoint(function, 0.01, 100);
        await driver.ConfigureSourceAsync(high, Token); await driver.EnableOutputAsync(Token); await driver.ConfigureSourceAsync(low, Token); await driver.ConfigureSourceAsync(high, Token);
        Assert.Equal(high, (await driver.GetSnapshotAsync(Token)).Source); Assert.Empty(peer.Errors); await driver.SafeStopAsync(Token); await driver.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("9.9e37")]
    [InlineData("malformed")]
    public async Task InvalidAcquisitionValueNeverBecomesADataRowAndStillStopsOverSynchronizedWire(string invalid)
    {
        await using var peer = new Keithley2450ScpiPeer(); peer.Override = (command, _) => command == Pair ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("0.0197," + invalid + ",200,0")) : null;
        var b = new Bench(peer); Exception error = invalid == "9.9e37" ? await Assert.ThrowsAsync<InstrumentReadException>(() => b.Runs.StartPlanAsync(b.Plan(), Token)) : await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(), Token)); Assert.Contains("numeric", error.Message);
        if (error is InstrumentReadException overrange) Assert.Equal(InstrumentReadFailureKind.Overrange, overrange.Kind);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
        Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task ReadErrorAndRefusedOutputOffRetainAllResourcesUntilFreshRecovery()
    {
        await using var peer = new Keithley2450ScpiPeer { RefuseOff = true }; peer.Override = (command, _) => command == Pair ? Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("1,NaN,200,0")) : null;
        var b = new Bench(peer, withField: true); var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(b.Plan(), Token));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("numeric", StringComparison.Ordinal)); Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("output-off", StringComparison.Ordinal));
        Assert.True(peer.Output); Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1)))); Assert.False((await b.Field!.GetSnapshotAsync(Token)).Moving);
        peer.RefuseOff = false; peer.Override = null; await b.Runs.RecoverSafetyAsync(Token); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.False(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow))); await b.Source.DisconnectAsync(Token);
    }
    [Fact]
    public async Task CanceledHardwareQueryCannotReuseLateReplyAndNeedsExplicitReconnectRecovery()
    {
        await using var peer = new Keithley2450ScpiPeer(); peer.Override = (command, token) => command == Pair ? WaitForever(token) : null;
        var b = new Bench(peer); using var cancellation = new CancellationTokenSource(); var running = b.Runs.StartPlanAsync(b.Plan(), cancellation.Token);
        await peer.Server.WaitForAsync(Pair); cancellation.Cancel(); await Assert.ThrowsAsync<IOException>(() => running);
        Assert.True(peer.Output); Assert.True(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.Empty((await b.SourceManager.GetDeviceStatusAsync(Token, b.Runs.LatestDataSet!.Id)).Values);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SourceManager.CaptureParametersAsync(Token, b.Runs.LatestDataSet.Id));
        peer.Override = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(peer.Output); Assert.Equal(2, peer.Server.Connections); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
        static async Task<byte[]?> WaitForever(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return null; }
    }
    [Fact]
    public async Task ActualProtocolRunArchivesHardwareIdentityAndPauseResumeRestoreRealOutput()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tcp-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(peer, listener: archive);
            using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(delay: true), Token);
            try
            {
                await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False(peer.Output);
                b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); Assert.True(peer.Output); b.Runs.Abort(); await running;
                Assert.False(peer.Output); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State);
                await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token);
                var device = Assert.Single(saved.DataSet.Instruments); Assert.Equal(peer.Identifier, device.Identifier); Assert.Equal("Hardware", device.Configuration["driver_kind"]);
                Assert.Equal(peer.ActualSource, saved.DataSet.Rows[0].Values[new("voltage")].Value); await fresh.ExportCsvAsync(saved.DataSet.Id, Path.Combine(folder, "data.csv"), Token);
                Assert.Contains("TEST-ONLY", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
            }
            finally { if (!running.IsCompleted) { b.Runs.Abort(); await Record.ExceptionAsync(() => running); } await b.Source.DisconnectAsync(Token); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task HardwareRejectedConfigurationStopsBeforeOutputOrDatasetCreation()
    {
        await using var peer = new Keithley2450ScpiPeer { IgnoreLevelWrite = true }; var b = new Bench(peer);
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(), Token)); Assert.Contains("differs", error.Message);
        Assert.Null(b.Runs.LatestDataSet); Assert.DoesNotContain(":OUTP ON", peer.Server.Commands); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); await b.Source.DisconnectAsync(Token);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public TcpKeithley2450SourceMeter Source { get; }
        public InstrumentManager SourceManager { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public MockAxisController? Field { get; }
        public Bench(Keithley2450ScpiPeer peer, bool withField = false, IRunResultListener? listener = null)
        {
            Source = new(new("tcp-smu"), "TCP source", peer.Server.Configuration()); SourceManager = new(Source, Hub); var sessions = new List<IDeviceSession> { SourceManager };
            if (withField) { Field = MockEnvironmentCatalog.Field(); sessions.Add(new AxisControllerSession(Field, Hub)); }
            Registry = new(sessions, Leases); Runs = new(SourceManager, Leases, Hub, listener is null ? null : [listener], deviceRegistry: Registry);
        }
        public ExperimentPlan Plan(bool delay = false)
        {
            var steps = new List<ExperimentStep> { new SourceOutputStep(new("on"), Source.Id, true), new ReadSourceSenseStep(new("read"), Source.Id, SourceFunction.Voltage, new("voltage"), new("current")) };
            if (Field is not null) steps.Add(new ReadStep(new("field"), [new(new("field_actual"), new(Field.Definition.Device.Id, Field.Definition.Readback.Id))]));
            if (delay) steps.Add(new DelayStep(new("delay"), TimeSpan.FromMinutes(1)));
            return new("TCP measurement", new SequenceStep(new("root"), steps), [new(Source.Id, SourceFunction.Voltage, new(0.02, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        }
    }
}

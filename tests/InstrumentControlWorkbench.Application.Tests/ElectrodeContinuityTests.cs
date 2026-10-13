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
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class ElectrodeContinuityTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private sealed class Driver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public DeviceDescriptor Descriptor => inner.Descriptor;
        public int Frames { get; private set; }
        public int Enables { get; private set; }
        public bool RefuseStop { get; set; }
        public bool Zero { get; set; }
        public Exception? Failure { get; set; }
        public bool FailureOnce { get; set; }
        public Func<Task>? AfterFrame { get; set; }
        public bool Stale { get; set; }
        public TaskCompletionSource Enabled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(double Voltage, bool Enabled)> Writes { get; } = [];
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken t) => inner.ConnectAsync(t);
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken t) => inner.GetSnapshotAsync(t);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken t) => inner.HeartbeatAsync(t);
        public async Task ConfigureSourceAsync(SourceSetpoint s, CancellationToken t) { Writes.Add((s.Level, (await inner.GetSnapshotAsync(t)).OutputEnabled)); await inner.ConfigureSourceAsync(s, t); }
        public Task ConfigureSenseAsync(SenseConfiguration s, CancellationToken t) => inner.ConfigureSenseAsync(s, t);
        public async Task EnableOutputAsync(CancellationToken t) { Enables++; await inner.EnableOutputAsync(t); Enabled.TrySetResult(); }
        public Task DisableOutputAsync(CancellationToken t) => inner.DisableOutputAsync(t);
        public Task SafeStopAsync(CancellationToken t) => RefuseStop && Frames != 0 ? Task.FromException(new IOException("OFF refused")) : inner.SafeStopAsync(t);
        public Task DisconnectAsync(CancellationToken t) => inner.DisconnectAsync(t);
        public Task<MeasurementValue> ReadAsync(CancellationToken t) => throw new InvalidOperationException("Use paired frames");
        public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken t)
        {
            Frames++; if (Failure is { } failure) { if (FailureOnce) Failure = null; throw failure; }
            var f = await inner.ReadSourceSenseAsync(t); if (AfterFrame is { } callback) await callback();
            return new(f.Function, f.SenseFunction, f.Source, Zero ? new(0, PhysicalUnit.Ampere) : f.Sense, Stale ? f.Timestamp.AddHours(-1) : f.Timestamp, f.Status, f.AcquisitionConfiguration);
        }
    }
    private sealed class Bench
    {
        public Driver Port { get; } = new(); public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5)); public DeviceRegistry Registry { get; }
        public MockMatrixSwitch Matrix { get; }
        public MeasurementRunService Runs { get; }
        public SourcePreparation Source => new(Port.Id, SourceFunction.Voltage, new(.1, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true));
        public Bench(IRunResultListener? archive = null)
        {
            Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 8, 10)], [Port.Id]));
            Registry = new([new InstrumentManager(Port, Hub), new MatrixSwitchSession(Matrix, Hub)], Leases); Runs = new(Registry, Hub, archive is null ? null : [archive]);
        }
        public ReadElectrodeContinuityStep Read(bool configured = true, double maximum = 1e6, TimeSpan? settle = null) => new(new("probe"), Source, new("channel1"), ElectrodePair.Meter, configured ? new(Matrix.Definition.Device.Id, [new(1, 1, 2), new(1, 2, 3)]) : null, settle ?? TimeSpan.Zero, new(maximum, PhysicalUnit.Ohm), new("status"), new("reason"), new("v"), new("i"), new("r"));
        public ExperimentPlan Plan(ReadElectrodeContinuityStep read) => new("Continuity", read.RequiredRoute is { } route ? new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), route.Device, route.Closed, TimeSpan.Zero), read]) : read, [Source]);
    }
    [Theory]
    [InlineData(false, false, false, 0, 0)]
    [InlineData(false, false, true, 1, 0)]
    [InlineData(true, false, false, 1, 1)]
    [InlineData(false, true, false, 1, 2)]
    public async Task ConnectedOpenZeroAndTypedOverrangeHaveExplicitEvidenceAndConfirmedOff(bool zero, bool overrange, bool highResistance, int status, int reason)
    {
        var b = new Bench(); b.Port.Zero = zero; if (overrange) b.Port.Failure = new InstrumentReadException(b.Port.Id, InstrumentReadFailureKind.Overrange, "overrange"); var read = b.Read(maximum: highResistance ? 1 : 1e6);
        await b.Runs.StartPlanAsync(b.Plan(read), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(status, row.Values[read.StatusColumn].Value); Assert.Equal(reason, row.Values[read.ReasonColumn].Value); Assert.Equal(!zero && !overrange, row.Values.ContainsKey(read.ResistanceColumn)); Assert.Equal(overrange ? 0 : 2, row.Quality.Count);
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun); Assert.Contains((.1, false), b.Port.Writes);
    }
    [Fact]
    public async Task TypedOverrangeContinuesToNextPairOnlyAfterConfirmedOff()
    {
        var b = new Bench(); var first = b.Read(); var second = new ReadElectrodeContinuityStep(new("next"), b.Source, new("channel2"), ElectrodePair.Source, first.RequiredRoute, TimeSpan.Zero, new(1e6, PhysicalUnit.Ohm), new("next.status"), new("next.reason"), new("next.v"), new("next.i"), new("next.r"));
        b.Port.Failure = new InstrumentReadException(b.Port.Id, InstrumentReadFailureKind.Overrange, "overrange"); b.Port.FailureOnce = true;
        var plan = new ExperimentPlan("Continue pairs", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), first.RequiredRoute!.Device, first.RequiredRoute.Closed, TimeSpan.Zero), first, second]), [b.Source]);
        await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(2, rows.Count); Assert.Equal(2, rows[0].Values[first.ReasonColumn].Value); Assert.Equal(0, rows[1].Values[second.StatusColumn].Value); Assert.Equal(2, b.Port.Frames); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task PauseDuringSettlingConfirmsOffAndResumeUsesOriginalConfiguration()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(b.Plan(b.Read(settle: TimeSpan.FromMilliseconds(250))), Token); await b.Port.Enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(timeout.Token)).Kind != Core.Notifications.WorkbenchNotificationKind.RunPaused) { }
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Leases.IsLeased(b.Port.Id, DateTimeOffset.UtcNow.AddYears(1))); b.Runs.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single(b.Runs.LatestDataSet.Rows); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData("preparation")]
    [InlineData("scan")]
    [InlineData("unguarded")]
    public async Task UnsafeAdmissionsRejectBeforeAnyConnection(string damage)
    {
        var b = new Bench(); var read = b.Read(); var plan = b.Plan(read);
        if (damage == "preparation") plan = new("Missing", read);
        if (damage == "scan") plan = new("Scan", new ScanStep(new("scan"), new(b.Port.Id, Keithley2450Parameters.SourceVoltage.Id), new("axis"), new([new(.1, PhysicalUnit.Volt)]), TimeSpan.Zero, read), [b.Source]);
        var runs = b.Runs;
        if (damage == "unguarded")
        {
            var matrix = new MockMatrixSwitch(new(new(new("matrix"), "K3706", "Unguarded", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 8, 10)], [new("other-source")]));
            runs = new(new DeviceRegistry([new InstrumentManager(b.Port, b.Hub), new InstrumentManager(new MockKeithley2450SourceMeter(new("other-source"), "Other"), b.Hub), new MatrixSwitchSession(matrix, b.Hub)], b.Leases), b.Hub);
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => runs.StartPlanAsync(plan, Token)); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task MissingPairHasNoReadEnableOrInventedElectricalValues()
    {
        var b = new Bench(); var read = b.Read(false); await b.Runs.StartPlanAsync(b.Plan(read), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(2, row.Values[read.StatusColumn].Value); Assert.Equal(3, row.Values[read.ReasonColumn].Value); Assert.Equal(0, b.Port.Enables); Assert.Equal(0, b.Port.Frames); Assert.Empty(row.Quality); Assert.DoesNotContain(read.VoltageColumn, row.Values.Keys);
    }
    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("other-device")]
    [InlineData("drift")]
    [InlineData("stale")]
    public async Task NonOverrangeFailuresAndConfigurationDriftNeverBecomeOpen(string failure)
    {
        var b = new Bench(); b.Port.Failure = failure switch { "io" => new IOException("wire disconnected"), "timeout" => new InstrumentReadException(b.Port.Id, InstrumentReadFailureKind.ReadTimeout, "timeout"), "other-device" => new InstrumentReadException(new("other"), InstrumentReadFailureKind.Overrange, "wrong device"), _ => null };
        if (failure == "drift") b.Port.AfterFrame = async () => { var state = await b.Port.GetSnapshotAsync(Token); await b.Port.ConfigureSenseAsync(state.Sense with { Nplc = 2 }, Token); };
        b.Port.Stale = failure == "stale"; await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task FailedOffDiscardsOutcomeAndKeepsBothClaimsUntilExplicitRecovery()
    {
        var b = new Bench(); b.Port.RefuseStop = true; await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Port.Id, DateTimeOffset.UtcNow.AddYears(1))); Assert.True(b.Leases.IsLeased(b.Matrix.Definition.Device.Id, DateTimeOffset.UtcNow.AddYears(1)));
        b.Port.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task AbortDuringSettlingStopsWithoutAResult()
    {
        var b = new Bench(); var running = b.Runs.StartPlanAsync(b.Plan(b.Read(settle: TimeSpan.FromSeconds(30))), Token); await b.Port.Enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task TerminalCountsAndFailedReadDoNotContinueToLaterPair()
    {
        var b = new Bench(); b.Port.Failure = new IOException("transport failed"); var first = b.Read();
        var second = new ReadElectrodeContinuityStep(new("next"), b.Source, new("channel2"), ElectrodePair.Gate, null, TimeSpan.Zero, new(1e6, PhysicalUnit.Ohm), new("next.status"), new("next.reason"), new("next.v"), new("next.i"), new("next.r"));
        var plan = new ExperimentPlan("Stop pairs", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), first.RequiredRoute!.Device, first.RequiredRoute.Closed, TimeSpan.Zero), first, second]), [b.Source]);
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.DoesNotContain(new ParameterId("next.status"), b.Runs.LatestBlackboard!.Keys);
    }
    [Fact]
    public async Task NativeSaveFreshArchiveAndCsvRetainPairIdentityAndOutcome()
    {
        var folder = Path.Combine(Path.GetTempPath(), "continuity-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive); var codec = new JsonExperimentPlanDocument(); var text = codec.Serialize(b.Plan(b.Read())); var plan = codec.Parse(text); Assert.Contains("Meter", text); Assert.Contains("channel1", text);
            await b.Runs.StartPlanAsync(plan, Token); var original = b.Runs.LatestDataSet!; await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(original.Id, Token); Assert.Equal(original.Rows[0].Values, saved.DataSet.Rows[0].Values); Assert.Equal(original.Rows[0].Quality, saved.DataSet.Rows[0].Quality); await fresh.ExportCsvAsync(original.Id, Path.Combine(folder, "out.csv"), Token); Assert.Contains("status [Dimensionless]", await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public async Task DeepDiagnosticAndCodecPreserveExistingPlanBoundary(int depth)
    {
        var b = new Bench(); ExperimentStep root = b.Read(false); for (var index = 0; index < depth; index++) root = new SequenceStep(new("nested" + index), [root]);
        var plan = new ExperimentPlan("Deep diagnostic", root, [b.Source]); var codec = new JsonExperimentPlanDocument(); var loaded = codec.Parse(codec.Serialize(plan)); Assert.Equal(depth + 1, b.Runs.PreviewPlan(loaded).MaximumStepExecutions); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("field")]
    [InlineData("enum")]
    [InlineData("null")]
    public void NativeDiagnosticRejectsUnknownEnumsFieldsAndNullRequiredValues(string damage)
    {
        var b = new Bench(); var codec = new JsonExperimentPlanDocument(); var document = JsonNode.Parse(codec.Serialize(b.Plan(b.Read(false))))!.AsObject(); var root = document["root"]!.AsObject();
        if (damage == "field") root["ignored"] = true; if (damage == "enum") root["pair"] = "Invented"; if (damage == "null") root["configuration"] = null; Assert.Throws<FormatException>(() => codec.Parse(document.ToJsonString()));
    }
    [Fact]
    public async Task FullMemoryWindowRejectsAnotherDiagnosticBeforeExcitation()
    {
        var b = new Bench(); var first = b.Read(false); var configured = b.Read(); var next = new ReadElectrodeContinuityStep(new("next"), b.Source, new("channel2"), ElectrodePair.Source, configured.RequiredRoute, TimeSpan.Zero, new(1e6, PhysicalUnit.Ohm), new("next.status"), new("next.reason"), new("next.v"), new("next.i"), new("next.r"));
        var plan = new ExperimentPlan("Bounded", new SequenceStep(new("root"), [first, new RouteMatrixStep(new("route"), next.RequiredRoute!.Device, next.RequiredRoute.Closed, TimeSpan.Zero), next]), [b.Source]); var runs = new MeasurementRunService(b.Registry, b.Hub, rowRetention: new(1, 8 * 1024 * 1024));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.StartPlanAsync(plan, Token)); Assert.Single(runs.LatestDataSet!.Rows); Assert.Equal(0, b.Port.Enables); Assert.Equal(0, b.Port.Frames); Assert.False(runs.HasActiveRun);
    }
    [Theory]
    [InlineData("threshold")]
    [InlineData("status")]
    [InlineData("unmeasured")]
    [InlineData("quality")]
    public async Task AlteredDiagnosticRowsAreRejected(string damage)
    {
        var b = new Bench(); var plan = b.Plan(b.Read()); await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; var row = data.Rows[0]; var values = row.Values.ToDictionary();
        if (damage == "threshold") values[new("r")] = new(0, PhysicalUnit.Ohm);
        if (damage == "status") values[new("status")] = new(1, PhysicalUnit.Dimensionless);
        if (damage == "unmeasured") values[new("reason")] = new(2, PhysicalUnit.Dimensionless);
        var bad = new MeasurementDataRow(row.Timestamp, values, quality: damage == "quality" ? [] : row.Quality); Assert.Throws<ArgumentException>(() => new RunDataRowValidator(plan, data.Schema).Validate(bad));
    }
}

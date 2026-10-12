using System.Text;
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
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class MeterSweepImportTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(SensingMode.TwoWire)]
    [InlineData(SensingMode.FourWire)]
    public async Task ViScanInheritsChannelReadsActualCurrentAndReopensForReplay(SensingMode mode)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vi-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var peer = new Keithley2450ScpiPeer(); await using var archive = new FileRunArchive(Path.Combine(folder, "runs"));
            var b = new Bench(peer, mode, archive); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), b.Runs, new PpmsExperimentPlanImporter());
            var document = workspace.Import(Scan(mode).ToJsonString(), b.Bindings);
            Assert.Contains("requiredRoute", document.Text); Assert.False(peer.Output); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
            var path = Path.Combine(folder, "vi.plan.json"); await workspace.SaveAsync(path, document.Text, Token);
            await b.Runs.StartPlanAsync((await workspace.LoadAsync(path, Token)).Plan, Token);
            var data = b.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count);
            Assert.Equal(new[] { -0.1, 0, 0.1 }, data.Rows.Select(r => r.Values[new("ppms.root.axis")].Value));
            Assert.All(data.Rows, row =>
            {
                Assert.Equal(new(peer.ActualSense, PhysicalUnit.Ampere), row.Values[new("ppms.root.0.value")]);
                var quality = Assert.Single(row.Quality); Assert.Equal(ReadingQualityState.ObservedNormal, row.QualityState);
                Assert.NotNull(quality.Status); Assert.Equal(mode, quality.Status.SensingMode);
                Assert.True(quality.AcquisitionConfiguration!.AveragingEnabled); Assert.Equal(4, quality.AcquisitionConfiguration.AveragingCount);
                Assert.Equal(3, row.Environment.Count); Assert.All(row.Environment, e => Assert.Null(e.Value));
            });
            var snapshot = await b.Source.GetSnapshotAsync(Token); Assert.Equal(2, snapshot.Sense.Nplc); Assert.Equal(new Quantity(0.001, PhysicalUnit.Ampere), snapshot.Sense.ManualRange);
            Assert.Equal(1, b.Matrix.Closes); Assert.False(peer.Output); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(data.Id, Token);
            Assert.Equal(data.Rows.Select(r => r.Values[new("ppms.root.0.value")]), stored.DataSet.Rows.Select(r => r.Values[new("ppms.root.0.value")]));
            Assert.Contains("requiredRoute", new JsonExperimentPlanDocument().Serialize(stored.DataSet.Plan!));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "vi.csv"), Token); Assert.Contains("ppms.root.axis", await File.ReadAllTextAsync(Path.Combine(folder, "vi.csv")));
            await b.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(2, b.Matrix.Closes);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("different_channel")]
    [InlineData("bypass")]
    [InlineData("external_to_routed")]
    [InlineData("meter_role")]
    [InlineData("missing_channel")]
    [InlineData("wrong_source")]
    public async Task InvalidViRouteContractRejectsBeforeAnyConnection(string fault)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var tree = Scan(SensingMode.TwoWire); var child = tree["children"]![0]!["params"]!;
        switch (fault)
        {
            case "different_channel": child["channel"] = "ch2"; break;
            case "bypass": child["channel"] = "none"; break;
            case "external_to_routed": tree["params"]!["channel"] = "none"; child["channel"] = "ch1"; break;
            case "meter_role": b.Bindings = b.WithRoutes(new(b.Source.Id, b.Route, new("other-meter"))); break;
            case "missing_channel": child["channel"] = "missing"; break;
            case "wrong_source": b.Bindings = b.WithRoutes(new(new("other-source"), b.Route)); break;
        }
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(tree.ToJsonString(), b.Bindings));
        Assert.Contains("ppms.root.0", error.Message); Assert.False(peer.Output); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteDriftDuringNativeTcpReadDiscardsRowAndValueAndRetainsFailedCleanup(bool failStop)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var reads = 0;
        peer.Override = (command, token) => command == ":MEAS? \"defbuffer1\", READ, SOURSTAT, STAT" ? Damage(token) : null;
        async Task<byte[]?> Damage(CancellationToken token)
        {
            reads++; await b.Matrix.OpenAsync([b.Route.Closed[0]], token); peer.RefuseOff = failStop;
            return Encoding.UTF8.GetBytes("0.000021,128,0\n");
        }
        var plan = new PpmsExperimentPlanImporter().Parse(Scan(SensingMode.TwoWire).ToJsonString(), b.Bindings);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Equal(1, reads); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("ppms.root.0.value")));
        Assert.Equal(failStop, b.Runs.HasActiveRun);
        if (failStop)
        {
            Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
            peer.RefuseOff = false; await b.Runs.RecoverSafetyAsync(Token);
        }
        Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViPauseAndAbortUseSharedLifecycleWithoutSwitchingRelays(bool resume)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); using var events = b.Hub.Subscribe();
        var tree = Scan(SensingMode.TwoWire); tree["params"]!["settle_s"] = 30;
        var run = b.Runs.StartPlanAsync(new PpmsExperimentPlanImporter().Parse(tree.ToJsonString(), b.Bindings), Token);
        try
        {
            await UntilStep(events, "ppms.root.settle"); Assert.True(peer.Output); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False(peer.Output);
            Assert.Equal(1, b.Matrix.Closes);
            if (resume) { b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed); Assert.True(peer.Output); }
            b.Runs.Abort(); await run; Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet!.State); Assert.Empty(b.Runs.LatestDataSet.Rows);
            Assert.False(peer.Output); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!run.IsCompleted) { b.Runs.Abort(); await run; } }
    }
    [Theory]
    [InlineData("unknown_matrix")]
    [InlineData("bounds")]
    public async Task NativeRequiredReadRouteIsPreflightedEvenWithoutSetupNode(string fault)
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        var matrix = fault == "unknown_matrix" ? new InstrumentId("missing") : b.Matrix.Definition.Device.Id;
        var read = new ReadStep(new("read"), [new(new("value"), new(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))], requiredRoute: new(matrix, [new(1, 1, fault == "bounds" ? 99 : 1)]));
        // Constructor rejects a matrix masquerading as the read instrument.
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(new("Invalid guarded read", read), Token));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task GuardedMultiChannelReadPublishesNoPartialBlackboardValuesAfterLateRouteDrift()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var reads = 0;
        var importer = new PpmsExperimentPlanImporter(); var codec = new JsonExperimentPlanDocument();
        var json = JsonNode.Parse(codec.Serialize(importer.Parse(Scan(SensingMode.TwoWire).ToJsonString(), b.Bindings)))!;
        JsonObject? read = null;
        void Find(JsonNode node)
        {
            if (node is JsonObject obj && obj["kind"]?.ToString() == "read") read = obj;
            else if (node is JsonObject o) { foreach (var value in o.Select(x => x.Value).Where(x => x is not null)) Find(value!); }
            else if (node is JsonArray array) { foreach (var value in array.Where(x => x is not null)) Find(value!); }
        }
        Find(json); Assert.NotNull(read); var second = read["channels"]![0]!.DeepClone(); second["column"] = "second.current"; read["channels"]!.AsArray().Add(second);
        peer.Override = (command, token) => command == ":MEAS? \"defbuffer1\", READ, SOURSTAT, STAT" ? Acquire(token) : null;
        async Task<byte[]?> Acquire(CancellationToken token)
        {
            if (++reads == 2) await b.Matrix.OpenAsync([b.Route.Closed[0]], token);
            return Encoding.UTF8.GetBytes("0.000021,128,0\n");
        }
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(codec.Parse(json.ToJsonString()), Token)); Assert.Equal(2, reads); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("ppms.root.0.value"))); Assert.False(b.Runs.LatestBlackboardSnapshot.Entries.ContainsKey(new("second.current")));
        Assert.False(peer.Output); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task NativeReadMatrixMustGuardItsDeclaredExcitationBeforeAdmission()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer);
        var read = new ReadStep(new("read"), [new(new("value"), new(b.Source.Id, Keithley2450Parameters.MeasuredCurrent.Id))], requiredRoute: new(b.Route.Device, b.Route.Closed), excitationSource: new("unguarded"));
        var codec = new JsonExperimentPlanDocument(); var plan = codec.Parse(codec.Serialize(new("Unguarded read", read)));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Contains("guard its declared excitation", error.Message);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    internal static JsonObject Scan(SensingMode mode) => new()
    {
        ["type"] = "scan",
        ["name"] = "scan_voltage",
        ["source_line"] = 1,
        ["params"] = new JsonObject { ["channel"] = "ch1", ["points"] = new JsonArray(-0.1, 0, 0.1), ["source_range_v"] = 0.2, ["compliance_a"] = 0.001, ["settle_s"] = 0 },
        ["children"] = new JsonArray(new JsonObject
        {
            ["type"] = "measurement",
            ["name"] = "measure_current",
            ["source_line"] = 2,
            ["params"] = new JsonObject
            { ["instrument"] = "k2450", ["sensing_mode"] = mode == SensingMode.FourWire ? "4wire" : "2wire", ["meter_range_a"] = 0.001, ["nplc"] = 2, ["filter_enabled"] = true, ["filter_count"] = 4 }
        })
    };
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private static async Task UntilStep(NotificationSubscription events, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var item = await events.Reader.ReadAsync(deadline.Token); if (item.Kind == WorkbenchNotificationKind.StepStarted && item.Step?.Value == id) return; }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public TcpKeithley2450SourceMeter Source { get; }
        public CountingMatrix Matrix { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentImportBindings Bindings { get; set; }
        private SourcePreparation Preparation { get; }
        public RouteMatrixStep Route => new(new("binding"), Matrix.Definition.Device.Id, [new(1, 1, 1), new(1, 2, 2)], TimeSpan.Zero);
        public Bench(Keithley2450ScpiPeer peer, SensingMode mode = SensingMode.TwoWire, IRunResultListener? listener = null)
        {
            Source = new(new("smu"), "Owned TCP VI test source", peer.Server.Configuration());
            Matrix = new(new(new(new("matrix"), "K3706", "VI matrix simulation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 4)], [Source.Id]));
            Registry = new([new InstrumentManager(Source, Hub), new MatrixSwitchSession(Matrix, Hub)], Leases); Runs = new(Registry, Hub, listener is null ? null : [listener]);
            Preparation = new(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, mode, 1, true));
            Bindings = WithRoutes(new(Source.Id, Route));
        }
        public ExperimentImportBindings WithRoutes(ImportChannelRoute route) => new([KeyValuePair.Create("k2450", Source.Id)], [], [Preparation],
            [KeyValuePair.Create("ch1", route), KeyValuePair.Create("ch2", new ImportChannelRoute(Source.Id, new(new("second"), Matrix.Definition.Device.Id, [new(1, 1, 3), new(1, 2, 4)], TimeSpan.Zero)))]);
    }
    private sealed class CountingMatrix(MatrixSwitchDefinition definition) : IMatrixSwitchPort
    {
        private readonly MockMatrixSwitch inner = new(definition); public int Closes { get; private set; }
        public MatrixSwitchDefinition Definition => inner.Definition;
        public Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task OpenAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token) => inner.OpenAsync(points, token);
        public Task CloseAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token) { Closes++; return inner.CloseAsync(points, token); }
    }
}

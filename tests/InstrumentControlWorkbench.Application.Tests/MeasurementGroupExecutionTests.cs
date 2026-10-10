using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
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

public sealed class MeasurementGroupExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static ExperimentPlan NativePlan(Bench b, TimeSpan delay = default)
    {
        var point = new MeasurementGroupStep(new("point"), [new("CH1", new ReadSourceSenseStep(new("first"), b.Source.Id, SourceFunction.Voltage, new("first.V"), new("first.I"))), new("CH1", new SequenceStep(new("second_member"),
            [new SetParameterStep(new("level"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new ConstantValue(new(0.02, PhysicalUnit.Volt))),
             new DelayStep(new("settle"), delay), new ReadSourceSenseStep(new("second"), b.Source.Id, SourceFunction.Voltage, new("second.V"), new("second.I"))]))]);
        return new("Wide point", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), point]), [b.Preparation]);
    }
    private static JsonObject Measurement(string name = "measure_voltage") => new() { ["type"] = "measurement", ["name"] = name, ["label"] = "CH1", ["params"] = new JsonObject { ["instrument"] = "k2450" } };
    private static JsonObject Tree(JsonArray children) => new()
    {
        ["type"] = "scan",
        ["name"] = "scan_voltage",
        ["params"] = new JsonObject { ["points"] = new JsonArray(0.01, 0.02), ["channel"] = "none", ["settle_s"] = 0 },
        ["children"] = children,
    };
    [Fact]
    public async Task NativePointUsesActualDistinctReadingsFirstTimeAndLeavesSourceOff()
    {
        var b = new Bench(); await b.Runs.StartPlanAsync(NativePlan(b), Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(3, rows.Count); var point = rows[2]; Assert.NotNull(point.MeasurementGroup);
        Assert.Equal(0.01, point.Values[new("first.V")].Value); Assert.Equal(0.02, point.Values[new("second.V")].Value);
        Assert.Equal(rows[0].Timestamp, point.Timestamp); Assert.Equal(rows[0].Values[new("elapsed")], point.Values[new("elapsed")]);
        Assert.Equal(new[] { "CH1", "CH1#2" }, point.MeasurementGroup!.Inputs.Select(i => i.Label));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ScanImportsConsecutiveAndNestedParallelReadingsIntoOneWidePointPerIteration()
    {
        var b = new Bench(); var parallel = new JsonObject { ["type"] = "parallel", ["source_line"] = 5, ["children"] = new JsonArray(Measurement("measure_current")) };
        var document = b.Workspace.Import(Tree(new(Measurement(), parallel)).ToJsonString(), b.Bindings);
        Assert.Equal(5, document.Plan.Metadata["ppms.root.1.source_line"].Value);
        await b.Runs.StartPlanAsync(document.Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(6, rows.Count); var points = rows.Where(r => r.MeasurementGroup is not null).ToArray(); Assert.Equal(2, points.Length);
        Assert.Equal(new[] { 1, 4 }, points.Select(p => p.MeasurementGroup!.Inputs[0].FirstInputRow));
        Assert.Equal(new[] { 0.01, 0.02 }, points.Select(p => p.Values[new("ppms.root.axis")].Value));
        Assert.All(points, p => { Assert.Equal(4, p.Values.Count); Assert.Equal(new[] { "CH1", "CH1#2" }, p.MeasurementGroup!.Inputs.Select(i => i.Label)); });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task NonMeasurementFlushesPendingMembersAndSingleReadRemainsSingleRow()
    {
        var b = new Bench(); var delay = new JsonObject { ["type"] = "delay", ["params"] = new JsonObject { ["delay_s"] = 0 } };
        var document = b.Workspace.Import(Tree(new(Measurement(), delay, Measurement(), Measurement("measure_current"))).ToJsonString(), b.Bindings);
        await b.Runs.StartPlanAsync(document.Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(8, rows.Count); var points = rows.Where(r => r.MeasurementGroup is not null).ToArray(); Assert.Equal(2, points.Length);
        Assert.Equal(new[] { 2, 6 }, points.Select(p => p.MeasurementGroup!.Inputs[0].FirstInputRow));
    }
    [Theory]
    [InlineData("wrapper_params")]
    [InlineData("outside_scan")]
    [InlineData("too_many")]
    public void UnsupportedParallelSemanticsAndExcessiveMembersRejectBeforeConnection(string damage)
    {
        var b = new Bench();
        var parallel = new JsonObject { ["type"] = "parallel", ["children"] = new JsonArray(Measurement(), Measurement("measure_current")) };
        if (damage == "wrapper_params") parallel["params"] = new JsonObject { ["concurrency"] = 2 };
        JsonObject tree = damage == "outside_scan" ? parallel : Tree(new(parallel));
        if (damage == "too_many") tree = Tree(new JsonArray(Enumerable.Range(0, 65).Select(_ => (JsonNode)Measurement()).ToArray()));
        Assert.Throws<FormatException>(() => b.Workspace.Import(tree.ToJsonString(), b.Bindings));
        Assert.Equal(0, b.Source.Connects); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task SecondReadFailureRetainsFirstRawAcquisitionWithoutInventingPoint()
    {
        var b = new Bench(); b.Source.FailReadAt = 2;
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(NativePlan(b), Token));
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Null(row.MeasurementGroup);
        Assert.Equal(0.01, row.Values[new("first.V")].Value); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task GroupReadAndCleanupFailureRetainBothErrorsAndOwnershipUntilRecovery()
    {
        var b = new Bench(); b.Source.FailReadAt = 2; b.Source.FailStop = true;
        var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(NativePlan(b), Token));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("group read failed", StringComparison.Ordinal));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("group stop failed", StringComparison.Ordinal));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        b.Source.FailStop = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseRetainsPartialGroupAndAbortOrResumeDoesNotDuplicateAcquisitions(bool abort)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var run = b.Runs.StartPlanAsync(NativePlan(b, TimeSpan.FromMilliseconds(400)), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(0.03, PhysicalUnit.Volt), Token));
        if (abort) b.Runs.Abort(); else b.Runs.Resume(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(abort ? 1 : 3, b.Runs.LatestDataSet.Rows.Count); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        if (!abort) Assert.NotNull(b.Runs.LatestDataSet.Rows[2].MeasurementGroup);
    }
    [Fact]
    public async Task NativeGroupArchivesReopensReexecutesAndExportsInputIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "measurement-point-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var document = b.Workspace.Inspect(NativePlan(b));
                await b.Workspace.SaveAsync(Path.Combine(folder, "plan.json"), document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(Path.Combine(folder, "plan.json"), Token)).Plan, Token); id = b.Runs.LatestDataSet!.Id;
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(id, Token);
            Assert.Equal(3, stored.DataSet.Rows.Count); Assert.Equal("CH1#2", stored.DataSet.Rows[2].MeasurementGroup!.Inputs[1].Label);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(3, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "point.csv"); await reopened.ExportCsvAsync(id, csv, Token);
            var text = await File.ReadAllTextAsync(csv); Assert.Contains("measurement_group_json", text); Assert.Contains("CH1#2", text);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ArchiveWriterRejectsPointValueTamperingBeforeWritingAFrame()
    {
        var folder = Path.Combine(Path.GetTempPath(), "point-writer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var b = new Bench(); await b.Runs.StartPlanAsync(NativePlan(b), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using var archive = new FileRunArchive(folder); await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
            await archive.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token); await archive.OnRowAsync(snapshot.Id, snapshot.Rows[1], Token);
            var point = snapshot.Rows[2]; var values = new Dictionary<ParameterId, Quantity>(point.Values) { [new("first.V")] = new(0.2, PhysicalUnit.Volt) };
            await Assert.ThrowsAsync<ArgumentException>(() => archive.OnRowAsync(snapshot.Id, new(point.Timestamp, values, measurementGroup: point.MeasurementGroup), Token));
            Assert.Equal(2, (await archive.LoadAsync(snapshot.Id, Token)).DataSet.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("value")]
    [InlineData("range")]
    [InlineData("label")]
    [InlineData("null_member")]
    [InlineData("mixed_provenance")]
    public async Task UnfinishedArchiveRejectsForgedPointAndPreservesExistingCsv(string corruption)
    {
        var folder = Path.Combine(Path.GetTempPath(), "point-reader-" + Guid.NewGuid().ToString("N"));
        try
        {
            var b = new Bench(); await b.Runs.StartPlanAsync(NativePlan(b), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(folder))
            {
                await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
                foreach (var row in snapshot.Rows) await writer.OnRowAsync(snapshot.Id, row, Token);
            }
            var path = Path.Combine(folder, snapshot.Id.Value.ToString("N"), "rows.ndjson"); var lines = await File.ReadAllLinesAsync(path);
            var point = JsonNode.Parse(lines[2])!;
            switch (corruption)
            {
                case "value": point["values"]!["first.V"]!["value"] = 0.3; break;
                case "range": point["measurementGroup"]!["inputs"]![0]!["firstInputRow"] = 2; point["measurementGroup"]!["inputs"]![1]!["firstInputRow"] = 3; break;
                case "label": point["measurementGroup"]!["inputs"]![1]!["label"] = "other"; break;
                case "null_member": point["measurementGroup"]!["inputs"]![1] = null; break;
                case "mixed_provenance": point["analysis"] = new JsonObject { ["step"] = "first", ["firstInputRow"] = 1, ["inputCount"] = 2, ["issues"] = "None", ["algorithmVersion"] = 1 }; break;
            }
            lines[2] = point.ToJsonString(); await File.WriteAllLinesAsync(path, lines);
            await using var reader = new FileRunArchive(folder);
            Assert.NotNull(await Record.ExceptionAsync(() => reader.LoadAsync(snapshot.Id, Token)));
            var csv = Path.Combine(folder, "existing.csv"); await File.WriteAllTextAsync(csv, "previous export");
            Assert.NotNull(await Record.ExceptionAsync(() => reader.ExportCsvAsync(snapshot.Id, csv, Token)));
            Assert.Equal("previous export", await File.ReadAllTextAsync(csv)); Assert.Empty(Directory.GetFiles(folder, "existing.csv.*.tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task LaterPointFailureArchivesCompletedPointAndPartialNextInput()
    {
        var folder = Path.Combine(Path.GetTempPath(), "partial-point-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); b.Source.FailReadAt = 4;
                var plan = NativePlan(b); var group = ((SequenceStep)plan.Root).Children[1];
                var repeated = new ExperimentPlan("Two points", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), new RepeatStep(new("repeat"), 2, group)]), [b.Preparation]);
                await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(repeated, Token));
                id = b.Runs.LatestDataSet!.Id; Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            }
            await using var reopened = new FileRunArchive(folder); var stored = await reopened.LoadAsync(id, Token);
            Assert.True(stored.IsFinalized); Assert.Equal(MeasurementRunState.Faulted, stored.DataSet.State); Assert.Equal(4, stored.DataSet.Rows.Count);
            Assert.NotNull(stored.DataSet.Rows[2].MeasurementGroup); Assert.Null(stored.DataSet.Rows[3].MeasurementGroup);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class Bench
    {
        public SourceProbe Source { get; } = new(); public NotificationHub Hub { get; } = new();
        public DeviceRegistry Registry { get; }
        public SourcePreparation Preparation { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public ExperimentImportBindings Bindings { get; }
        public Bench(FileRunArchive? archive = null)
        {
            var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var manager = new InstrumentManager(Source, Hub);
            var field = MockEnvironmentCatalog.Field(); Registry = new([manager, new AxisControllerSession(field, Hub)], leases);
            Runs = new(manager, leases, Hub, archive is null ? null : [archive], deviceRegistry: Registry);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
            Preparation = new(Source.Id, SourceFunction.Voltage, new(0.01, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true));
            Bindings = new([KeyValuePair.Create("k2450", Source.Id)], [KeyValuePair.Create("field", field.Definition)], [Preparation]);
        }
    }
    private sealed class SourceProbe : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new(); private int reads;
        public int? FailReadAt { get; set; }
        public bool FailStop { get; set; }
        public int Connects { get; private set; }
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public Core.Devices.DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) { Connects++; return inner.ConnectAsync(token); }
        public Task ConfigureSourceAsync(SourceSetpoint value, CancellationToken token) => inner.ConfigureSourceAsync(value, token);
        public Task ConfigureSenseAsync(SenseConfiguration value, CancellationToken token) => inner.ConfigureSenseAsync(value, token);
        public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token);
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token)
        { if (++reads == FailReadAt) throw new IOException("group read failed"); return inner.ReadSourceSenseAsync(token); }
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task SafeStopAsync(CancellationToken token)
        { if (FailStop && reads > 0) throw new IOException("group stop failed"); return inner.SafeStopAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
}

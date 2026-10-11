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
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class EnvironmentAcquisitionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static ExperimentPlan Plan(Bench b, bool grouped = true)
    {
        var first = new ReadResistanceStep(new("r"), b.Source.Id, SourceFunction.Voltage, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere), b.Reads("first"));
        var second = new ReadSourceSenseStep(new("pair"), b.Source.Id, SourceFunction.Voltage, new("V2"), new("I2"), b.Reads("second"));
        ExperimentStep body = grouped ? new MeasurementGroupStep(new("point"), [new("CH1", first), new("CH1", second)]) : first;
        return new("Environment capture", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Source.Id, true), body]), [b.Preparation]);
    }
    [Fact]
    public async Task ActualReadbacksAndIndependentTimesSurviveWidePointWithoutTargetFallback()
    {
        var b = new Bench(); await b.Runs.StartPlanAsync(Plan(b), Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(3, rows.Count); Assert.All(rows.Take(2), row => Assert.Equal(3, row.Environment.Count));
        Assert.Equal(4.2, rows[0].Values[new("first.Temperature")].Value, 10);
        Assert.Equal(4.3, rows[1].Values[new("second.Temperature")].Value, 10);
        Assert.NotEqual(300, rows[0].Values[new("first.Temperature")].Value);
        Assert.All(rows[0].Environment, o => { Assert.NotNull(o.Value); Assert.Null(o.Error); Assert.True(o.CapturedAt >= rows[0].Timestamp); });
        Assert.Equal(rows[0].Timestamp, rows[2].Timestamp); Assert.Equal(6, rows[2].Environment.Count);
        Assert.Equal(rows.Take(2).SelectMany(r => r.Environment), rows[2].Environment);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task AbortDuringEnvironmentReadCancelsCaptureAndStillConfirmsOutputOff()
    {
        var b = new Bench(); b.Temperature.BlockNextCapture = true;
        var task = b.Runs.StartPlanAsync(Plan(b), Token);
        await b.Temperature.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task EnvironmentReadFailureAndHoldFailureRetainDataAndRequireFreshRecovery()
    {
        var b = new Bench(); b.Temperature.Failure = "read"; b.Temperature.FailHold = true;
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(Plan(b, grouped: false), Token));
        Assert.NotNull(error); Assert.Contains("hold failed", error.ToString());
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Contains("sensor read failed", row.Environment[0].Error);
        Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        b.Temperature.FailHold = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task RepositoryFileRunsFreshEnvironmentPointsAndReopensForExecution()
    {
        var folder = Path.Combine(Path.GetTempPath(), "env-example-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive);
                var bindings = new ExperimentImportBindings([KeyValuePair.Create("k2450", b.Source.Id)],
                    [KeyValuePair.Create("temperature", b.Temperature.Definition), KeyValuePair.Create("field", b.Field.Definition), KeyValuePair.Create("angle", b.Angle.Definition)], [b.Preparation]);
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-environment-point-import.json"), bindings, Token);
                await b.Runs.StartPlanAsync(document.Plan, Token); var data = b.Runs.LatestDataSet!; id = data.Id;
                Assert.Equal(6, data.Rows.Count); Assert.Equal(4, data.Instruments.Count);
                var points = data.Rows.Where(r => r.MeasurementGroup is not null).ToArray(); Assert.Equal(2, points.Length);
                Assert.All(points, row => { Assert.Equal(6, row.Environment.Count); Assert.All(row.Environment, o => Assert.Null(o.Error)); });
                Assert.NotEqual(points[0].Environment[0].Value, points[0].Environment[3].Value);
                Assert.Equal(new[] { "CH1", "CH1#2" }, points[0].MeasurementGroup!.Inputs.Select(i => i.Label));
            }
            await using var reader = new FileRunArchive(folder); var stored = await reader.LoadAsync(id, Token); Assert.Equal(6, stored.DataSet.Rows.Count);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(6, fresh.Runs.LatestDataSet!.Rows.Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("read")]
    [InlineData("unit")]
    [InlineData("range")]
    public async Task EnvironmentFaultIsExplicitUnavailableEvidenceWhileElectricalDataAndLaterReadSurvive(string failure)
    {
        var b = new Bench(); b.Temperature.Failure = failure;
        await b.Runs.StartPlanAsync(Plan(b), Token); var rows = b.Runs.LatestDataSet!.Rows;
        var observation = Assert.Single(rows[0].Environment, o => o.Role == EnvironmentRole.Temperature);
        Assert.Null(observation.Value); Assert.NotNull(observation.Error); Assert.False(rows[0].Values.ContainsKey(observation.Column));
        Assert.Equal(0.02, rows[0].Values[new("V")].Value); Assert.True(rows[0].Values[new("I")].Value > 0);
        Assert.True(rows[0].Values[new("R")].Value > 0); Assert.NotNull(rows[1].Environment[0].Value);
        Assert.Null(rows[2].Environment[0].Value); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ImportedMeasurementCapturesAllThreeAndPreservesExplicitUnboundRolesAndLegacyFlags()
    {
        var b = new Bench();
        var bindings = new ExperimentImportBindings([KeyValuePair.Create("k2450", b.Source.Id)], [KeyValuePair.Create("field", b.Field.Definition)], [b.Preparation]);
        var text = """{"type":"measurement","name":"meas_r","params":{"mode":"volt_dc","channel":"none","record_temperature":true,"record_field":true,"record_angle":true,"settle_s":0}}""";
        var imported = b.Workspace.Import(text, bindings); await b.Runs.StartPlanAsync(imported.Plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(3, row.Environment.Count);
        Assert.NotNull(row.Environment.Single(o => o.Role == EnvironmentRole.Field).Value);
        Assert.All(row.Environment.Where(o => o.Role != EnvironmentRole.Field), o => { Assert.Null(o.Source); Assert.Null(o.Value); Assert.Contains("not bound", o.Error); });
        Assert.DoesNotContain(b.Runs.LatestDataSet.Instruments, i => i.InstrumentId == b.Temperature.Definition.Device.Id);
    }
    [Theory]
    [InlineData("target")]
    [InlineData("wrong_role")]
    [InlineData("shared_policy")]
    public void InvalidContextRejectsAtPreviewBeforeConnection(string fault)
    {
        var b = new Bench(); var reads = b.Reads("env").ToArray();
        if (fault == "target") reads[0] = new(EnvironmentRole.Temperature, reads[0].Column, new(b.Temperature.Definition.Device.Id, b.Temperature.Definition.Target.Id));
        if (fault == "wrong_role") reads[0] = new(EnvironmentRole.Temperature, reads[0].Column, new(b.Field.Definition.Device.Id, b.Field.Definition.Readback.Id));
        var primary = new ReadSourceSenseStep(new("a"), b.Source.Id, SourceFunction.Voltage, new("V"), new("I"), reads);
        ExperimentStep root = fault == "shared_policy" ? new SequenceStep(new("root"), [primary, new ReadSourceSenseStep(new("b"), b.Source.Id, SourceFunction.Voltage, new("V"), new("I"))]) : primary;
        Assert.Throws<ArgumentException>(() => b.Runs.PreviewPlan(new("Bad context", root, [b.Preparation]))); Assert.Equal(0, b.Source.Connects);
    }
    [Fact]
    public async Task FreshArchiveReexecutesActualContextAndExportsErrorsAndTimes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "env-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); b.Temperature.Failure = "read"; var native = b.Workspace.Inspect(Plan(b));
                var planPath = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(planPath, native.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(planPath, Token)).Plan, Token); id = b.Runs.LatestDataSet!.Id;
            }
            await using var reader = new FileRunArchive(folder); var stored = await reader.LoadAsync(id, Token);
            Assert.Equal(6, stored.DataSet.Rows[2].Environment.Count); Assert.Contains("sensor read failed", stored.DataSet.Rows[0].Environment[0].Error);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.All(fresh.Runs.LatestDataSet!.Rows[0].Environment, o => Assert.Null(o.Error));
            var csv = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
            try { await reader.ExportCsvAsync(id, csv, Token); var output = await File.ReadAllTextAsync(csv); Assert.Contains("environment_json", output); Assert.Contains("sensor read failed", output); Assert.Contains("capturedAt", output); }
            finally { File.Delete(csv); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("source")]
    [InlineData("value")]
    [InlineData("missing")]
    [InlineData("time")]
    [InlineData("error")]
    public async Task UnfinishedArchiveRejectsForgedEnvironmentAndPreservesPriorExport(string fault)
    {
        var folder = Path.Combine(Path.GetTempPath(), "env-forged-" + Guid.NewGuid().ToString("N")); var csv = folder + ".csv";
        try
        {
            var b = new Bench(); await b.Runs.StartPlanAsync(Plan(b), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(folder))
            {
                await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [], Blackboard = null }, Token);
                foreach (var row in snapshot.Rows) await writer.OnRowAsync(snapshot.Id, row, Token);
            }
            var path = Path.Combine(folder, snapshot.Id.Value.ToString("N"), "rows.ndjson"); var lines = await File.ReadAllLinesAsync(path); var point = JsonNode.Parse(lines[2])!;
            switch (fault)
            {
                case "source": point["environment"]![0]!["parameter"] = "temperature.target"; break;
                case "value": point["environment"]![0]!["value"]!["value"] = 200; break;
                case "missing": point.AsObject().Remove("environment"); break;
                case "time": point["environment"]![0]!["capturedAt"] = DateTimeOffset.UtcNow.ToString("O"); break;
                case "error": point["environment"]![0]!["error"] = "forged error"; break;
            }
            lines[2] = point.ToJsonString(); await File.WriteAllLinesAsync(path, lines); await File.WriteAllTextAsync(csv, "old export");
            await using var reader = new FileRunArchive(folder); Assert.NotNull(await Record.ExceptionAsync(() => reader.LoadAsync(snapshot.Id, Token)));
            Assert.NotNull(await Record.ExceptionAsync(() => reader.ExportCsvAsync(snapshot.Id, csv, Token))); Assert.Equal("old export", await File.ReadAllTextAsync(csv));
        }
        finally { File.Delete(csv); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Bench
    {
        public SourceProbe Source { get; } = new(); public AxisProbe Temperature { get; } = new();
        public MockAxisController Field { get; } = MockEnvironmentCatalog.Field(); public MockAxisController Angle { get; } = MockEnvironmentCatalog.Angle();
        public SourcePreparation Preparation { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench(FileRunArchive? archive = null)
        {
            var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var manager = new InstrumentManager(Source, hub);
            var registry = new DeviceRegistry([manager, new AxisControllerSession(Temperature, hub), new AxisControllerSession(Field, hub), new AxisControllerSession(Angle, hub)], leases);
            Runs = new(manager, leases, hub, archive is null ? null : [archive], deviceRegistry: registry); Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
            Preparation = new(Source.Id, SourceFunction.Voltage, new(0.02, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true));
            Source.AfterRead = () => Temperature.CaptureNext = true;
        }
        public IReadOnlyList<EnvironmentRead> Reads(string prefix) => [new(EnvironmentRole.Temperature, new(prefix + ".Temperature"), new(Temperature.Definition.Device.Id, Temperature.Definition.Readback.Id)),
            new(EnvironmentRole.Field, new(prefix + ".Field"), new(Field.Definition.Device.Id, Field.Definition.Readback.Id)), new(EnvironmentRole.Angle, new(prefix + ".Angle"), new(Angle.Definition.Device.Id, Angle.Definition.Readback.Id))];
    }
    private sealed class AxisProbe : IAxisControllerPort
    {
        private readonly MockAxisController inner = MockEnvironmentCatalog.Temperature(); private int captures;
        public bool CaptureNext { get; set; }
        public string? Failure { get; set; }
        public bool FailHold { get; set; }
        public bool BlockNextCapture { get; set; }
        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AxisControllerDefinition Definition => new(inner.Definition.Device, inner.Definition.Target, inner.Definition.Readback, inner.Definition.Rate);
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public async Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            var snapshot = await inner.GetSnapshotAsync(token); if (!CaptureNext) return snapshot; CaptureNext = false;
            CaptureStarted.TrySetResult();
            if (BlockNextCapture) { BlockNextCapture = false; await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token); }
            var failure = Failure; Failure = null; if (failure == "read") throw new IOException("sensor read failed");
            return snapshot with { Readback = new(failure == "range" ? 1000 : 4.2 + captures++ * 0.1, failure == "unit" ? PhysicalUnit.Tesla : PhysicalUnit.Kelvin) };
        }
        public Task SetTargetAsync(Quantity value, CancellationToken token) => inner.SetTargetAsync(value, token);
        public Task SetRateAsync(Quantity value, CancellationToken token) => inner.SetRateAsync(value, token);
        public Task HoldAsync(CancellationToken token) => FailHold ? throw new IOException("hold failed") : inner.HoldAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
    private sealed class SourceProbe : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new(); public Action? AfterRead { get; set; }
        public int Connects { get; private set; }
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) { Connects++; return inner.ConnectAsync(token); }
        public Task ConfigureSourceAsync(SourceSetpoint value, CancellationToken token) => inner.ConfigureSourceAsync(value, token);
        public Task ConfigureSenseAsync(SenseConfiguration value, CancellationToken token) => inner.ConfigureSenseAsync(value, token);
        public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token); public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token) { var frame = await inner.ReadSourceSenseAsync(token); AfterRead?.Invoke(); return frame; }
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task SafeStopAsync(CancellationToken token) => inner.SafeStopAsync(token); public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
}

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

public sealed class SourceSenseExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Fact]
    public async Task IvRowsAndPointsUseActualReadbacksFromOneFrameAndKeepRequestedAxis()
    {
        var b = new Bench(); await b.Runs.StartIvSweepAsync(new("actual IV", 0.1, 0.3, 3, 0.001, 0), Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, b.Driver.Frames); Assert.Equal(0, b.Driver.IndependentReads);
        var points = new List<MeasurementPoint>(); while (b.Hub.Reader.TryRead(out var fact)) if (fact.Point is { } point) points.Add(point);
        Assert.Equal(3, points.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i].Values[Keithley2450Parameters.SourceVoltage.Id].Value * 0.5, rows[i].Values[Keithley2450Parameters.MeasuredVoltage.Id].Value);
            Assert.Equal(rows[i].Values[Keithley2450Parameters.MeasuredVoltage.Id].Value, points[i].SourceValue);
            Assert.Equal(rows[i].Values[Keithley2450Parameters.MeasuredCurrent.Id].Value, points[i].MeasuredValue);
            Assert.Equal(b.Driver.FrameTime, rows[i].Timestamp); Assert.Equal(rows[i].Timestamp, points[i].Timestamp);
        }
        var column = b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == Keithley2450Parameters.MeasuredVoltage.Id);
        Assert.Equal(Keithley2450Parameters.MeasuredVoltage.Id, column.Source!.Parameter); Assert.Contains(Keithley2450Parameters.SourceVoltage.Id, column.DependsOn);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task GenericPairStepsSupportBothFunctionsAndTypedVariables(SourceFunction function)
    {
        var b = new Bench(); var root = new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Driver.Id, true), Pair(b, function),
            new AssignValueStep(new("copy"), new("copy_voltage"), new VariableValue(new("voltage")))]);
        await b.Runs.StartPlanAsync(Plan(b, root, function), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(PhysicalUnit.Volt, row.Values[new("voltage")].Unit); Assert.Equal(PhysicalUnit.Ampere, row.Values[new("current")].Unit);
        Assert.Equal(1, b.Driver.Frames); Assert.Equal(0, b.Driver.IndependentReads); Assert.Equal(b.Driver.FrameTime, row.Timestamp);
        Assert.Equal(function == SourceFunction.Voltage ? 0.05 : 5e-7, row.Values[new(function == SourceFunction.Voltage ? "voltage" : "current")].Value);
    }
    [Fact]
    public async Task ZeroCurrentIsValidForPairedIvAcquisition()
    {
        var b = new Bench(); await b.Runs.StartIvSweepAsync(new("zero IV", 0, 0, 2, 0.001, 0), Token);
        Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Equal(0, row.Values[Keithley2450Parameters.MeasuredCurrent.Id].Value));
        Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }
    [Theory]
    [InlineData("missing_preparation")]
    [InlineData("wrong_function")]
    [InlineData("same_column")]
    [InlineData("axis_collision")]
    public async Task InvalidPairPlanIsRejectedBeforeHardware(string damage)
    {
        var b = new Bench(); var read = Pair(b, damage == "wrong_function" ? SourceFunction.Current : SourceFunction.Voltage); ExperimentStep root = read;
        if (damage == "same_column")
        { Assert.Throws<ArgumentException>(() => new ReadSourceSenseStep(new("bad"), b.Driver.Id, SourceFunction.Voltage, new("same"), new("same"))); return; }
        if (damage == "axis_collision") root = new ScanStep(new("scan"), new(b.Driver.Id, Keithley2450Parameters.SourceVoltage.Id), read.VoltageColumn, new([new(0.1, PhysicalUnit.Volt)]), TimeSpan.Zero, read);
        var plan = damage == "missing_preparation" ? new("bad", root) : Plan(b, root);
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).Connected); Assert.Equal(0, b.Driver.Frames);
    }
    [Theory]
    [InlineData("mode")]
    [InlineData("function")]
    [InlineData("output")]
    [InlineData("level")]
    [InlineData("compliance")]
    [InlineData("nplc")]
    public async Task StateChangesDuringFrameRejectTheRowAndStillStopOutput(string damage)
    {
        var b = new Bench(); b.Driver.AfterRead = async () =>
        {
            var snapshot = await b.Driver.GetSnapshotAsync(Token);
            if (damage == "mode") await b.Driver.ConfigureSenseAsync(snapshot.Sense with { Mode = SensingMode.TwoWire }, Token);
            if (damage == "function") await b.Driver.ConfigureSourceAsync(new(SourceFunction.Current, 1e-6, 10), Token);
            if (damage == "output") await b.Driver.DisableOutputAsync(Token);
            if (damage == "level") await b.Driver.ConfigureSourceAsync(snapshot.Source with { Level = 0.2 }, Token);
            if (damage == "compliance") await b.Driver.ConfigureSourceAsync(snapshot.Source with { Compliance = 0.002 }, Token);
            if (damage == "nplc") await b.Driver.ConfigureSenseAsync(snapshot.Sense with { Nplc = 2 }, Token);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartIvSweepAsync(new("changed", 0.1, 0.2, 2, 0.001, 0), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task FrameFaultRetainsPreviousRowsAndTheOriginalError()
    {
        var b = new Bench(); b.Driver.FailAt = 2; b.Driver.Error = new IOException("paired transport failed");
        var error = await Assert.ThrowsAsync<IOException>(() => b.Runs.StartIvSweepAsync(new("partial", 0.1, 0.3, 3, 0.001, 0), Token));
        Assert.Equal("paired transport failed", error.Message); Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(2, b.Driver.Frames);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task WrongFrameFunctionDoesNotBecomeAnIncorrectlyLabelledRow()
    {
        var b = new Bench(); b.Driver.Map = _ => new(SourceFunction.Current, SenseFunction.Voltage, new(1e-6, PhysicalUnit.Ampere), new(1, PhysicalUnit.Volt), b.Driver.FrameTime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartIvSweepAsync(new("wrong frame", 0.1, 0.2, 2, 0.001, 0), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task PairPlanAndActualIvDataSurviveFreshArchiveAndCsv()
    {
        using var folder = new TemporaryFolder(); RunId id;
        await using (var archive = new FileRunArchive(Path.Combine(folder.Path, "runs")))
        {
            var b = new Bench(archive); await b.Runs.StartIvSweepAsync(new("saved IV", 0.1, 0.3, 3, 0.001, 0), Token); id = b.Runs.LatestDataSet!.Id;
            Assert.Contains("read_source_sense", new JsonExperimentPlanDocument().Serialize(b.Runs.LatestDataSet.Plan!));
        }
        await using var reopened = new FileRunArchive(Path.Combine(folder.Path, "runs")); var loaded = await reopened.LoadAsync(id, Token);
        Assert.Equal(3, loaded.DataSet.Rows.Count); Assert.Equal(0.05, loaded.DataSet.Rows[0].Values[Keithley2450Parameters.MeasuredVoltage.Id].Value);
        var csv = Path.Combine(folder.Path, "iv.csv"); await reopened.ExportCsvAsync(id, csv, Token);
        var text = await File.ReadAllTextAsync(csv); Assert.Contains("k2450.source_voltage [Volt]", text); Assert.Contains("k2450.measured_voltage [Volt]", text);
        var codec = new JsonExperimentPlanDocument(); var restored = codec.Parse(codec.Serialize(loaded.DataSet.Plan!)); var next = new Bench(); await next.Runs.StartPlanAsync(restored, Token);
        Assert.Equal(3, next.Driver.Frames); Assert.Equal(0, next.Driver.IndependentReads);
    }
    [Fact]
    public async Task CurrentSourceDiskPlanPreservesBothNestedAxesAndActualPairValues()
    {
        var driver = new PairDriver();
        driver.Map = frame => new(frame.Function, frame.SenseFunction, new(frame.Source.Value * 0.5, frame.Source.Unit), new(frame.Source.Value * 200, PhysicalUnit.Volt), driver.FrameTime);
        var hub = new NotificationHub(); var manager = new InstrumentManager(driver, hub);
        var field = MockEnvironmentCatalog.Field(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var devices = new DeviceRegistry([manager, new AxisControllerSession(field, hub)], leases);
        var runs = new MeasurementRunService(manager, leases, hub, deviceRegistry: devices);
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "field-source-sense.plan.json"), Token);
        await runs.StartPlanAsync(plan, Token);
        var data = runs.LatestDataSet!; Assert.Equal(9, data.Rows.Count); Assert.Equal(9, driver.Frames); Assert.Equal(0, driver.IndependentReads);
        Assert.Equal(new[] { 0.0, 0, 0, 0.01, 0.01, 0.01, 0.02, 0.02, 0.02 }, data.Rows.Select(r => r.Values[new("field_axis")].Value));
        Assert.Equal(new[] { -1e-6, 0, 1e-6, -1e-6, 0, 1e-6, -1e-6, 0, 1e-6 }, data.Rows.Select(r => r.Values[new("current_axis")].Value));
        Assert.All(data.Rows, row =>
        {
            Assert.Equal(row.Values[new("current_axis")].Value * 0.5, row.Values[new("actual_current")].Value);
            Assert.Equal(row.Values[new("current_axis")].Value * 200, row.Values[new("measured_voltage")].Value);
            Assert.Equal(driver.FrameTime, row.Timestamp); data.Schema.ValidateRow(row);
        });
        Assert.All(data.Schema.Columns.Where(c => c.Parameter.Id == new ParameterId("actual_current") || c.Parameter.Id == new ParameterId("measured_voltage")),
            c => Assert.Equal(new[] { new ParameterId("field_axis"), new ParameterId("current_axis"), new ParameterId("elapsed") }, c.DependsOn));
        Assert.False((await driver.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(runs.HasActiveRun);
        var held = await field.GetSnapshotAsync(Token); Assert.False(held.Moving); Assert.Equal(held.Readback, held.Target);
    }
    [Theory]
    [InlineData("mode")]
    [InlineData("function")]
    [InlineData("level")]
    [InlineData("compliance")]
    [InlineData("nplc")]
    public async Task PausedSourceConfigurationChangesCannotReenableOutput(string damage)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var run = b.Runs.StartIvSweepAsync(new("pause guard", 0.1, 0.2, 2, 0.001, 30), Token);
        await Until(events, WorkbenchNotificationKind.MeasurementPointReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        var snapshot = await b.Driver.GetSnapshotAsync(Token); Assert.False(snapshot.OutputEnabled);
        if (damage == "mode") await b.Driver.ConfigureSenseAsync(snapshot.Sense with { Mode = SensingMode.TwoWire }, Token);
        if (damage == "function") await b.Driver.ConfigureSourceAsync(new(SourceFunction.Current, 1e-6, 10), Token);
        if (damage == "level") await b.Driver.ConfigureSourceAsync(snapshot.Source with { Level = 0.2 }, Token);
        if (damage == "compliance") await b.Driver.ConfigureSourceAsync(snapshot.Source with { Compliance = 0.002 }, Token);
        if (damage == "nplc") await b.Driver.ConfigureSenseAsync(snapshot.Sense with { Nplc = 2 }, Token);
        b.Runs.Resume(); await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, b.Driver.EnableCalls); Assert.Equal(1, b.Driver.Frames); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task PairAcquisitionWithoutOutputIsRejectedWithoutTriggeringAFrame()
    {
        var b = new Bench(); await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(Plan(b, Pair(b, SourceFunction.Voltage)), Token));
        Assert.Equal(0, b.Driver.Frames); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task PairedIvAbortRetainsTheDeliveredFrameAndStopsOutput()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var run = b.Runs.StartIvSweepAsync(new("abort pair", 0.1, 0.3, 3, 0.001, 30), Token);
        await Until(events, WorkbenchNotificationKind.MeasurementPointReceived); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Driver.Frames); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private static ReadSourceSenseStep Pair(Bench b, SourceFunction function) => new(new("pair"), b.Driver.Id, function, new("voltage"), new("current"));
    private static ExperimentPlan Plan(Bench b, ExperimentStep root, SourceFunction function = SourceFunction.Voltage) => new("paired plan", root,
        [new(b.Driver.Id, function, new(function == SourceFunction.Voltage ? 0.1 : 1e-6, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere),
            new(function == SourceFunction.Voltage ? 0.001 : 10, function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage, SensingMode.FourWire, 1, true))]);
    private sealed class Bench
    {
        public PairDriver Driver { get; } = new(); public NotificationHub Hub { get; } = new(); public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null) => Runs = new(new InstrumentManager(Driver, Hub), new InstrumentLeaseService(TimeSpan.FromSeconds(5)), Hub,
            archive is null ? null : [archive]);
    }
    private sealed class PairDriver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public int Frames { get; private set; }
        public int IndependentReads { get; private set; }
        public int EnableCalls { get; private set; }
        public int FailAt { get; set; }
        public Exception? Error { get; set; }
        public Func<Task>? AfterRead { get; set; }
        public Func<SourceSenseReading, SourceSenseReading>? Map { get; set; }
        public DateTimeOffset FrameTime { get; } = DateTimeOffset.UtcNow;
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public Core.Devices.DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint source, CancellationToken token) => inner.ConfigureSourceAsync(source, token);
        public Task ConfigureSenseAsync(SenseConfiguration sense, CancellationToken token) => inner.ConfigureSenseAsync(sense, token);
        public Task EnableOutputAsync(CancellationToken token) { EnableCalls++; return inner.EnableOutputAsync(token); }
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task SafeStopAsync(CancellationToken token) => inner.SafeStopAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) { IndependentReads++; throw new InvalidOperationException("Independent acquisition is forbidden by this paired fixture."); }
        public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token)
        {
            Frames++; if (Frames == FailAt && Error is { } error) throw error;
            var frame = await inner.ReadSourceSenseAsync(token);
            var result = Map?.Invoke(frame) ?? new(frame.Function, frame.SenseFunction, new(frame.Source.Value * 0.5, frame.Source.Unit), new(frame.Sense.Value * 0.25, frame.Sense.Unit), FrameTime);
            if (AfterRead is { } callback) await callback(); return result;
        }
    }
    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"paired-test-{Guid.NewGuid():N}");
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}

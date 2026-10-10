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
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class ResistanceExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(SensingMode.TwoWire)]
    [InlineData(SensingMode.FourWire)]
    public async Task VoltageRecipeUsesActualPairedReadbackAndConfiguresRequestedSensing(SensingMode mode)
    {
        var b = new Bench();
        await b.Runs.StartVoltageResistanceAsync(new("R", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), mode, settle: TimeSpan.Zero), Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(0.099, row.Values[new("voltage")].Value, 10);
        Assert.Equal(1e-5, row.Values[new("current")].Value, 10);
        Assert.Equal(9900, row.Values[new("resistance")].Value, 7);
        Assert.Equal(PhysicalUnit.Ohm, row.Values[new("resistance")].Unit);
        Assert.Equal(1, b.Driver.PairedReads);
        Assert.Equal(0, b.Driver.IndependentReads);
        Assert.Equal(mode, (await b.Driver.GetSnapshotAsync(Token)).Sense.Mode);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task CurrentSourceUsesActualCurrentAndMeasuredVoltage()
    {
        var b = new Bench();
        var plan = b.Prepared(new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Driver.Id, true),
            Read(b.Driver.Id, SourceFunction.Current)]), SourceFunction.Current);
        await b.Runs.StartPlanAsync(plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(0.001 * 0.99, row.Values[new("current")].Value, 10);
        Assert.Equal(0.001 * 10000, row.Values[new("voltage")].Value, 10);
        Assert.Equal(row.Values[new("voltage")].Value / row.Values[new("current")].Value, row.Values[new("resistance")].Value);
    }

    [Fact]
    public async Task NestedFieldVoltageScanRoundTripsDocumentAndArchiveWithoutLosingDependencies()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"icw-resistance-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var body = new SequenceStep(new("point"), [new DelayStep(new("settle"), TimeSpan.Zero), Read(b.Driver.Id)]);
                var voltage = new ScanStep(new("voltage_scan"), new(b.Driver.Id, Keithley2450Parameters.SourceVoltage.Id),
                    new("bias"), new([new(0.1, PhysicalUnit.Volt), new(0.2, PhysicalUnit.Volt), new(-0.1, PhysicalUnit.Volt)]), TimeSpan.Zero, body);
                var field = new ScanStep(new("field_scan"), new(b.Axis.Definition.Device.Id, b.Axis.Definition.Target.Id),
                    new("field"), ScanValues.Linear(new(-0.1, PhysicalUnit.Tesla), new(0.1, PhysicalUnit.Tesla), 3), TimeSpan.Zero, voltage);
                var codec = new JsonExperimentPlanDocument();
                var path = Path.Combine(folder, "plan.json");
                await codec.SaveAsync(path, b.Prepared(new SequenceStep(new("root"), [new SourceOutputStep(new("on"), b.Driver.Id, true), field])), Token);
                var plan = await codec.LoadAsync(path, Token);
                await b.Runs.StartPlanAsync(plan, Token, new(sampleName: "resistance sample"));
                id = b.Runs.LatestDataSet!.Id;
                Assert.Equal(9, b.Runs.LatestDataSet.Rows.Count);
                Assert.Equal(9, b.Driver.PairedReads);
                Assert.Equal(1, b.Driver.Enables);
                Assert.All(b.Runs.LatestDataSet.Rows, b.Runs.LatestDataSet.Schema.ValidateRow);
                var r = b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("resistance"));
                Assert.Contains(new ParameterId("field"), r.DependsOn);
                Assert.Contains(new ParameterId("bias"), r.DependsOn);
                Assert.Contains(new ParameterId("voltage"), r.DependsOn);
                Assert.Contains(new ParameterId("current"), r.DependsOn);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs"));
            var saved = await reopened.LoadAsync(id, Token);
            Assert.Equal(9, saved.DataSet.Rows.Count);
            Assert.All(saved.DataSet.Rows, row => Assert.Equal(9900, row.Values[new("resistance")].Value, 7));
            Assert.Equal("resistance sample", saved.DataSet.Metadata!.SampleName);
            await reopened.ExportCsvAsync(id, Path.Combine(folder, "R.csv"), Token);
            Assert.Contains("resistance [Ohm]", await File.ReadAllTextAsync(Path.Combine(folder, "R.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task RepositoryResistanceExampleRunsFromDiskOnFreshSimulationSessions()
    {
        var hub = new NotificationHub(); var source = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(source, hub); var axis = MockEnvironmentCatalog.Field();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var runs = new MeasurementRunService(manager, leases, hub, deviceRegistry: new DeviceRegistry([manager, new AxisControllerSession(axis, hub)], leases));
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "field-resistance.plan.json"), Token);
        await runs.StartPlanAsync(plan, Token);
        Assert.Equal(9, runs.LatestDataSet!.Rows.Count);
        Assert.All(runs.LatestDataSet.Rows, row =>
        {
            runs.LatestDataSet.Schema.ValidateRow(row);
            Assert.Equal(row.Values[new("voltage")].Value / row.Values[new("current")].Value, row.Values[new("resistance")].Value);
            Assert.Contains(new ParameterId("field_axis"), row.Values.Keys);
            Assert.Contains(new ParameterId("voltage_axis"), row.Values.Keys);
        });
        Assert.False((await source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(runs.HasActiveRun);
    }

    [Theory]
    [InlineData("zero_current")]
    [InlineData("wrong_function")]
    [InlineData("read_error")]
    public async Task InvalidAcquisitionCommitsNoRowAndStillStopsOutput(string failure)
    {
        var b = new Bench();
        b.Driver.Failure = failure;
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartVoltageResistanceAsync(
            new("fault", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), settle: TimeSpan.Zero), Token)));
        Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task CleanupFailureRetainsOwnershipAndPreservesOriginalReadError()
    {
        var b = new Bench(); b.Driver.Failure = "read_error"; b.Driver.StopFails = true;
        var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartVoltageResistanceAsync(
            new("fault", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), settle: TimeSpan.Zero), Token));
        Assert.Contains(error.InnerExceptions, ex => ex.Message == "paired read failed");
        Assert.Contains(error.InnerExceptions, ex => ex.Message == "stop failed");
        Assert.True(b.Runs.HasActiveRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Manager.ReadSourceSenseAsync(SourceFunction.Voltage, Token));
        b.Driver.StopFails = false;
        await b.Runs.RecoverSafetyAsync(Token);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Theory]
    [InlineData("unprepared")]
    [InlineData("wrong_mode")]
    [InlineData("bad_scan_envelope")]
    [InlineData("wrong_sense")]
    public async Task InvalidPlanRejectedBeforeConnectingOrAcquiringResources(string invalid)
    {
        var b = new Bench();
        var plan = invalid switch
        {
            "unprepared" => new ExperimentPlan("bad", Read(b.Driver.Id)),
            "wrong_mode" => b.Prepared(Read(b.Driver.Id, SourceFunction.Current)),
            "wrong_sense" => new ExperimentPlan("bad", Read(b.Driver.Id),
                [new(b.Driver.Id, SourceFunction.Voltage, new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Voltage, SensingMode.TwoWire, 1, true))]),
            _ => new ExperimentPlan("bad", new ScanStep(new("scan"), new(b.Driver.Id, Keithley2450Parameters.SourceVoltage.Id),
                    new("bias"), new([new(100, PhysicalUnit.Volt)]), TimeSpan.Zero, Read(b.Driver.Id)),
                [new(b.Driver.Id, SourceFunction.Voltage, new(0.1, PhysicalUnit.Volt), new(1, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]),
        };
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)));
        Assert.Equal(0, b.Driver.Connects); Assert.Equal(0, b.Driver.Enables);
        Assert.False(b.Runs.HasActiveRun); Assert.Empty(b.Runs.History.Runs);
    }

    [Fact]
    public async Task PauseDuringSettlingStopsOutputAndAbortKeepsTheEmptyPartialArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"icw-resistance-abort-{Guid.NewGuid():N}");
        try
        {
            await using var archive = new FileRunArchive(folder);
            var b = new Bench(archive);
            using var events = b.Hub.Subscribe();
            var task = b.Runs.StartVoltageResistanceAsync(new("pause", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), settle: TimeSpan.FromSeconds(30)), Token);
            await Until(events, WorkbenchNotificationKind.StepStarted, "settle");
            b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
            b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, b.Driver.PairedReads);
            var saved = await archive.LoadAsync(b.Runs.LatestDataSet!.Id, Token);
            Assert.Equal(MeasurementRunState.Aborted, saved.DataSet.State);
            Assert.Empty(saved.DataSet.Rows);
            Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ChangingComplianceBeforeHighVoltageDoesNotUseStalePreparationForValidation()
    {
        var b = new Bench();
        var plan = new ExperimentPlan("adjust compliance", new SequenceStep(new("root"),
            [new SetParameterStep(new("limit"), new(b.Driver.Id, Keithley2450Parameters.CurrentCompliance.Id), new ConstantValue(new(0.01, PhysicalUnit.Ampere))),
             new SourceOutputStep(new("on"), b.Driver.Id, true),
             new ScanStep(new("scan"), new(b.Driver.Id, Keithley2450Parameters.SourceVoltage.Id), new("bias"), new([new(100, PhysicalUnit.Volt)]), TimeSpan.Zero, Read(b.Driver.Id))]),
            [new(b.Driver.Id, SourceFunction.Voltage, new(0.1, PhysicalUnit.Volt), new(1, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(9900, b.Runs.LatestDataSet.Rows[0].Values[new("resistance")].Value, 7);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Fact]
    public async Task DynamicSourceValueChecksTheActualCoupledEnvelopeBeforeSendingIt()
    {
        var b = new Bench();
        var plan = new ExperimentPlan("dynamic invalid", new SequenceStep(new("root"),
            [new AssignValueStep(new("assign"), new("bad_bias"), new ConstantValue(new(100, PhysicalUnit.Volt))),
             new SourceOutputStep(new("on"), b.Driver.Id, true),
             new SetParameterStep(new("set"), new(b.Driver.Id, Keithley2450Parameters.SourceVoltage.Id), new VariableValue(new("bad_bias"))),
             Read(b.Driver.Id)]),
            [new(b.Driver.Id, SourceFunction.Voltage, new(0.1, PhysicalUnit.Volt), new(1, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.Equal(1, b.Driver.SourceConfigurations);
        Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(0.1, (await b.Driver.GetSnapshotAsync(Token)).Source.Level);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PairedAcquisitionInvalidatesAnEarlierStopConfirmation()
    {
        var b = new Bench();
        var resources = await b.Devices.AcquireRunAsync([b.Driver.Id], RunId.New(), Token);
        await resources.ConnectAllAsync(Token);
        await resources.ConfigureSourceMeterAsync(b.Driver.Id, new(SourceFunction.Voltage, 0.1, 1e-4),
            new(SenseFunction.Current, SensingMode.TwoWire, 1, true), Token);
        await resources.EnableSourceOutputAsync(b.Driver.Id, Token);
        Assert.Empty(await resources.StopAllAsync(Token));
        // Model an operator enabling the physical front panel after that stop.
        await b.Driver.EnableOutputAsync(Token);
        await resources.ReadSourceSenseAsync(b.Driver.Id, SourceFunction.Voltage, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ReleaseAsync(Token));
        Assert.Empty(await resources.StopAllAsync(Token));
        Assert.Equal(2, b.Driver.StopCalls);
        Assert.False((await b.Driver.GetSnapshotAsync(Token)).OutputEnabled);
        await resources.ReleaseAsync(Token);
    }

    [Fact]
    public async Task PreCancelledRecipeDoesNotTouchDriver()
    {
        var b = new Bench(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await b.Runs.StartVoltageResistanceAsync(
            new("cancel", new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere)), cancelled.Token);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.Null(b.Runs.LatestDataSet);
        Assert.Equal(0, b.Driver.Connects); Assert.Equal(0, b.Driver.Enables); Assert.Equal(0, b.Driver.PairedReads);
    }

    private static ReadResistanceStep Read(InstrumentId id, SourceFunction function = SourceFunction.Voltage)
        => new(new("read"), id, function, new("voltage"), new("current"), new("resistance"), new(2.220446049250313e-16, PhysicalUnit.Ampere));
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind, string? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var fact = await events.Reader.ReadAsync(deadline.Token);
            if (fact.Kind == kind && (step is null || fact.Step?.Value == step)) return;
        }
    }
    private sealed class Bench
    {
        public PairedDriver Driver { get; } = new();
        public MockAxisController Axis { get; } = MockEnvironmentCatalog.Field();
        public NotificationHub Hub { get; } = new();
        public InstrumentManager Manager { get; }
        public MeasurementRunService Runs { get; }
        public DeviceRegistry Devices { get; }
        public Bench(IRunResultListener? archive = null)
        {
            Manager = new(Driver, Hub);
            var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            Devices = new DeviceRegistry([Manager, new AxisControllerSession(Axis, Hub)], leases);
            Runs = new(Manager, leases, Hub, archive is null ? [] : [archive], deviceRegistry: Devices);
        }
        public ExperimentPlan Prepared(ExperimentStep root, SourceFunction function = SourceFunction.Voltage) => new("R plan", root,
            [function == SourceFunction.Voltage ? new(Driver.Id, function, new(0.1, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true)) :
                new(Driver.Id, function, new(0.001, PhysicalUnit.Ampere), new(20, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.FourWire, 1, true))]);
    }
    private sealed class PairedDriver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public string? Failure { get; set; }
        public bool StopFails { get; set; }
        public int PairedReads { get; private set; }
        public int IndependentReads { get; private set; }
        public int Connects { get; private set; }
        public int Enables { get; private set; }
        public int SourceConfigurations { get; private set; }
        public int StopCalls { get; private set; }
        public InstrumentId Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) { Connects++; return inner.ConnectAsync(token); }
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint source, CancellationToken token) { SourceConfigurations++; return inner.ConfigureSourceAsync(source, token); }
        public Task ConfigureSenseAsync(SenseConfiguration sense, CancellationToken token) => inner.ConfigureSenseAsync(sense, token);
        public Task EnableOutputAsync(CancellationToken token) { Enables++; return inner.EnableOutputAsync(token); }
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) { IndependentReads++; return inner.ReadAsync(token); }
        public async Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); PairedReads++;
            if (Failure == "read_error") throw new IOException("paired read failed");
            var snapshot = await inner.GetSnapshotAsync(token);
            if (Failure == "wrong_function") return new(SourceFunction.Current, SenseFunction.Voltage, new(0.001, PhysicalUnit.Ampere), new(0.1, PhysicalUnit.Volt), DateTimeOffset.UtcNow);
            var voltage = snapshot.Source.Function == SourceFunction.Voltage;
            return new(snapshot.Source.Function, voltage ? SenseFunction.Current : SenseFunction.Voltage,
                new(snapshot.Source.Level * 0.99, voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere),
                new(Failure == "zero_current" ? 0 : voltage ? snapshot.Source.Level / 10000 : snapshot.Source.Level * 10000,
                    voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), DateTimeOffset.UtcNow);
        }
        public Task SafeStopAsync(CancellationToken token) { StopCalls++; return StopFails ? Task.FromException(new IOException("stop failed")) : inner.SafeStopAsync(token); }
    }
}

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

public sealed partial class CurrentResistanceExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Theory]
    [InlineData(CurrentReference.ProgrammedSetpoint, false)]
    [InlineData(CurrentReference.MeasuredSourceReadback, false)]
    [InlineData(CurrentReference.ProgrammedSetpoint, true)]
    [InlineData(CurrentReference.MeasuredSourceReadback, true)]
    public async Task NativeReferenceRouteConfigurationAndIndependentTimesSurviveFreshArchiveAndCsv(CurrentReference reference, bool routed)
    {
        var folder = Path.Combine(Path.GetTempPath(), "current-resistance-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive); b.Peer.ActualSource = 8e-6;
            var plan = b.Plan(b.Read(reference, route: routed ? b.Route : null)); var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            await b.Runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var e = Assert.Single(row.IndependentResistance);
            Assert.Equal(reference == CurrentReference.ProgrammedSetpoint ? 100 : 125, e.Resistance.Value, 8); Assert.Equal(reference, e.CurrentReference);
            Assert.Equal(1e-5, e.SourceConfiguration.Level); Assert.Equal(routed ? b.Route : null, e.Route); Assert.True(e.SourceCapturedAt < e.VoltageCapturedAt);
            Assert.Equal(2, row.Quality.Count); Assert.DoesNotContain(row.Quality, q => q.Column == new ParameterId("R"));
            Assert.Equal(reference == CurrentReference.ProgrammedSetpoint ? ReadingQualityState.Unobserved : ReadingQualityState.PartiallyObserved, row.QualityState);
            var currentColumn = b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("I")); Assert.Equal(reference == CurrentReference.ProgrammedSetpoint ? Keithley2450Parameters.SourceCurrent.Id : Keithley2450Parameters.MeasuredCurrent.Id, currentColumn.Source!.Parameter);
            Assert.False(b.Peer.Output); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
            if (reference == CurrentReference.ProgrammedSetpoint) Assert.DoesNotContain(b.Peer.Server.Commands, c => c.StartsWith(":MEAS?", StringComparison.Ordinal));
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token); Assert.Equal(e, Assert.Single(Assert.Single(loaded.DataSet.Rows).IndependentResistance));
            await fresh.ExportCsvAsync(b.Runs.LatestDataSet.Id, Path.Combine(folder, "data.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")); Assert.Contains("independent_resistance_json", csv); Assert.Contains(reference.ToString(), csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("source")]
    [InlineData("meter")]
    [InlineData("route")]
    public async Task DriftAcrossIndependentAcquisitionDiscardsResistanceAndStopsSource(string drift)
    {
        await using var b = new Bench();
        b.Meter.OnRead = async token =>
        {
            if (drift == "source") await b.Source.ConfigureSourceAsync(new(SourceFunction.Current, 2e-5, 10), token);
            if (drift == "meter") await b.Meter.ConfigureAsync(new(2, null, 1, false, 10, AveragingMode.Repeat, 0.1), token);
            if (drift == "route") await b.Matrix.OpenAsync([b.Route.Closed[0]], token);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read(route: b.Route)), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStopOfEitherInstrumentRetainsAllDevicesUntilExplicitRecovery(bool sourceFailure)
    {
        await using var b = new Bench(); b.Meter.OnRead = _ => { b.Peer.RefuseOff = sourceFailure; b.Meter.RefuseStop = !sourceFailure; return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Read(route: b.Route)), Token)); Assert.True(b.Runs.HasActiveRun);
        Assert.All(b.Registry.Devices, d => Assert.True(b.Leases.IsLeased(d.Id, DateTimeOffset.UtcNow.AddYears(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Meter.Descriptor.Id, Keithley2182Parameters.Nplc.Id, new(2, PhysicalUnit.Dimensionless), Token));
        b.Peer.RefuseOff = false; b.Meter.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.False(b.Peer.Output); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task MeterFailureAndCancellationPreserveAbortCleanupWithoutPartialResistance()
    {
        await using var b = new Bench(); b.Meter.OnRead = _ => throw new IOException("Independent voltage failed.");
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read()), Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Peer.Output);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); b.Meter.OnRead = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read()), Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await running;
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State); Assert.Empty(b.Runs.LatestDataSet.Rows); Assert.False(b.Peer.Output); Assert.True(b.Meter.LastStopTokenWasLive);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1e-18)]
    public async Task UndefinedMeasuredCurrentFailsBeforeExternalMeterRead(double current)
    {
        await using var b = new Bench(); b.Peer.ActualSource = current;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(b.Plan(b.Read(CurrentReference.MeasuredSourceReadback)), Token)); Assert.Equal(0, b.Meter.Reads); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Peer.Output);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedMeterDriftIsConfirmedBeforeRestoringExcitation(bool drift)
    {
        await using var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(b.Read(id: "first"), new DelayStep(new("wait"), TimeSpan.FromSeconds(30)), b.Read(id: "second")), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); var enables = b.Peer.Server.Commands.Count(c => c == ":OUTP ON");
        if (drift) await b.Meter.ConfigureAsync(new(2, null, 1, false, 10, AveragingMode.Repeat, 0.1), Token);
        b.Runs.Resume();
        if (drift) { await Assert.ThrowsAsync<InvalidOperationException>(() => running); Assert.Equal(enables, b.Peer.Server.Commands.Count(c => c == ":OUTP ON")); }
        else { await Until(events, WorkbenchNotificationKind.RunResumed); b.Runs.Abort(); await running; }
        Assert.False(b.Peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private sealed class Bench : IAsyncDisposable
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public Keithley2450ScpiPeer Peer { get; } = new(); public TcpKeithley2450SourceMeter Source { get; }
        public FaultMeter Meter { get; } = new(); public MockMatrixSwitch Matrix { get; }
        public MatrixRouteExpectation Route { get; }
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? listener = null)
        {
            Source = new(new("source"), "Loopback current source", Peer.Server.Configuration());
            Matrix = new(new(new(new("matrix"), "K3706", "Simulation matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 4)], [Source.Id]));
            Route = new(Matrix.Definition.Device.Id, [new(1, 1, 1), new(1, 2, 2), new(1, 3, 1), new(1, 4, 2)]);
            Registry = new([new InstrumentManager(Source, Hub), new Keithley2182MeterSession(Meter, Hub), new MatrixSwitchSession(Matrix, Hub)], Leases);
            Runs = new(Registry, Hub, listener is null ? null : [listener]);
        }
        public SourcePreparation Preparation => new(Source.Id, SourceFunction.Current, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.TwoWire, 1, true));
        public ReadCurrentResistanceStep Read(CurrentReference reference = CurrentReference.ProgrammedSetpoint, string id = "read", MatrixRouteExpectation? route = null)
            => new(new(id), Source.Id, Meter.Descriptor.Id, reference, new(id == "read" ? "V" : id + ".V"), new(id == "read" ? "I" : id + ".I"), new(id == "read" ? "R" : id + ".R"), new(2.220446049250313e-16, PhysicalUnit.Ampere), new ConstantValue(new(1e-5, PhysicalUnit.Ampere)), route);
        public ExperimentPlan Plan(params ExperimentStep[] steps)
        {
            var route = steps.OfType<ReadCurrentResistanceStep>().Select(s => s.RequiredRoute).FirstOrDefault(r => r is not null);
            return new("Current resistance", new SequenceStep(new("root"), [.. route is null ? Array.Empty<ExperimentStep>() : new ExperimentStep[] { new RouteMatrixStep(new("route"), route.Device, route.Closed, TimeSpan.Zero) }, new SourceOutputStep(new("on"), Source.Id, true), .. steps]), [Preparation]);
        }
        public async ValueTask DisposeAsync()
        {
            Peer.RefuseOff = false; Meter.RefuseStop = false;
            if (Runs.HasActiveRun) await Runs.RecoverSafetyAsync(Token, reconnect: true);
            if ((await Source.GetSnapshotAsync(Token)).Connected) await Source.DisconnectAsync(Token);
            await Peer.DisposeAsync();
        }
    }
    private sealed class FaultMeter : IK2182MeterPort
    {
        private readonly MockKeithley2182Meter inner = new(new("meter"), "Explicit voltage simulation");
        public DeviceDescriptor Descriptor => inner.Descriptor; public Func<CancellationToken, Task>? OnRead { get; set; }
        public bool RefuseStop { get; set; }
        public bool LastStopTokenWasLive { get; private set; }
        public int Reads { get; private set; }
        public Task<K2182MeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<K2182MeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task SelectChannelAsync(int channel, CancellationToken token) => inner.SelectChannelAsync(channel, token);
        public Task ConfigureAsync(Keithley2182Settings settings, CancellationToken token) => inner.ConfigureAsync(settings, token);
        public async Task<K2182VoltageReading> ReadAsync(CancellationToken token) { Reads++; if (OnRead is { } action) await action(token); return await inner.ReadAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task AbortAcquisitionAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); LastStopTokenWasLive = true; if (RefuseStop) throw new IOException("Meter idle not confirmed."); return inner.AbortAcquisitionAsync(token); }
    }
}

using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Sr830AcExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly MatrixCrossPoint PathA = new(1, 1, 1);
    private static readonly MatrixCrossPoint PathB = new(1, 1, 2);
    private static readonly MatrixCrossPoint Unrelated = new(1, 2, 2);
    private static readonly Sr830ExcitationSettings Excitation = new(new(17, PhysicalUnit.Hertz), new(.01, PhysicalUnit.Volt), 2);
    private sealed class Bench
    {
        public List<string> Events { get; } = [];
        public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public ProbeMeter Meter { get; }
        public ProbeMatrix Matrix { get; }
        public ProbeSource Source { get; } = new();
        public DeviceRegistry Registry { get; }
        public MeasurementRunService Runs { get; }
        public Bench(bool baseline = false, bool capability = true, bool activePort = true, IRunResultListener? listener = null, RunRowRetention? retention = null, TimeSpan? safetyTimeout = null, InstrumentId? capabilityGenerator = null)
        {
            Meter = new(Events); var cap = capability ? new MatrixAcIsolationCapability(capabilityGenerator ?? Meter.Descriptor.Id, [PathA, PathB], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)) : null;
            var definition = new MatrixSwitchDefinition(new(new("matrix"), "MATRIX", "Isolation simulation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 4)], [Source.Id], cap);
            Matrix = new(definition, Events, baseline ? [PathA, PathB, Unrelated] : [Unrelated]);
            Registry = new([new Sr830MeterSession(activePort ? Meter : new PassivePort(Meter), Hub), new MatrixSwitchSession(Matrix, Hub), new InstrumentManager(Source, Hub)], Leases);
            Runs = new(Registry, Hub, listener is null ? null : [listener], safetyTimeout, retention);
        }
        public ReadSr830AcFrameStep Read(string id = "ac", TimeSpan? settle = null, IEnumerable<MatrixCrossPoint>? paths = null) => new(new(id), Meter.Descriptor.Id, Matrix.Definition.Device.Id, paths ?? [PathA, PathB], Excitation, Sr830InputMode.VoltageAB, new(id), settle ?? TimeSpan.Zero);
        public ExperimentPlan Plan(params ExperimentStep[] steps) => new("Atomic AC", new SequenceStep(new("root"), steps));
        public void AssertReserved()
        {
            Assert.True(Runs.HasActiveRun); foreach (var id in new[] { Meter.Descriptor.Id, Matrix.Definition.Device.Id, Source.Id }) Assert.True(Leases.IsLeased(id, DateTimeOffset.UtcNow.AddYears(1)));
        }
        public void AssertReleased()
        {
            Assert.False(Runs.HasActiveRun); foreach (var id in new[] { Meter.Descriptor.Id, Matrix.Definition.Device.Id, Source.Id }) Assert.False(Leases.IsLeased(id, DateTimeOffset.UtcNow));
        }
    }
    private sealed class ProbeMeter(List<string> events) : ISr830ExcitationPort
    {
        private readonly MockSr830ExcitationMeter inner = new(new("lockin"), "Active SR830 simulation");
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public int Connections { get; private set; }
        public int Configurations { get; private set; }
        public int Reads { get; private set; }
        public int ReturnedFrames { get; private set; }
        public int Aborts { get; private set; }
        public Sr830ReferenceReadback? ReferenceOverride { get; set; }
        public Sr830InputSettings? InputOverride { get; set; }
        public Func<CancellationToken, Task>? OnRead { get; set; }
        public Func<Task>? AfterRead { get; set; }
        public Func<Task>? AfterConfigure { get; set; }
        public bool IgnoreReadCancellation { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token) { Connections++; await inner.ConnectAsync(token); return await GetSnapshotAsync(token); }
        public async Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            var s = await inner.GetSnapshotAsync(token); return new(s.Connected, s.Acquiring, s.Connected ? InputOverride ?? s.Settings : null, s.Timestamp, s.Connected ? ReferenceOverride ?? s.Reference : null);
        }
        public Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token) { events.Add("configure-input"); return inner.ConfigureAsync(settings, token); }
        public async Task ConfigureExcitationAsync(Sr830ExcitationSettings settings, CancellationToken token)
        {
            Configurations++; events.Add("configure-excitation"); await inner.ConfigureExcitationAsync(settings, token); if (AfterConfigure is not null) await AfterConfigure();
        }
        public async Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token)
        {
            Reads++; events.Add("frame"); ReadEntered.TrySetResult();
            try { if (OnRead is not null) await OnRead(token); var frame = await inner.ReadFrameAsync(IgnoreReadCancellation ? Token : token); ReturnedFrames++; if (AfterRead is not null) await AfterRead(); return frame; }
            finally { ReadFinished.TrySetResult(); }
        }
        public Task AbortAcquisitionAsync(CancellationToken token) { Aborts++; events.Add("abort"); return inner.AbortAcquisitionAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
    private sealed class PassivePort(ProbeMeter inner) : ISr830MeterPort
    {
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token) => inner.ConfigureAsync(settings, token);
        public Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token) => inner.ReadFrameAsync(token);
        public Task AbortAcquisitionAsync(CancellationToken token) => inner.AbortAcquisitionAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
    private sealed class ProbeMatrix(MatrixSwitchDefinition definition, List<string> events, MatrixCrossPoint[] initial) : IMatrixSwitchPort
    {
        private readonly MockMatrixSwitch inner = new(definition, initial);
        public MatrixSwitchDefinition Definition => definition;
        public int Connections { get; private set; }
        public int Opens { get; private set; }
        public int Closes { get; private set; }
        public bool RefuseOpen { get; set; }
        public bool Unknown { get; set; }
        public bool IgnoreSnapshotCancellation { get; set; }
        public Func<Task>? AfterClose { get; set; }
        public TaskCompletionSource? OpenGate { get; set; }
        public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) { Connections++; await inner.ConnectAsync(token); return await GetSnapshotAsync(token); }
        public async Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            var s = await inner.GetSnapshotAsync(IgnoreSnapshotCancellation ? Token : token); if (s.Connected) events.Add(Unknown ? "observe-unknown" : s.Closed.Intersect([PathA, PathB]).Any() ? "observe-closed" : "observe-open");
            return Unknown ? new(definition, s.Connected, false, [], s.Timestamp) : s;
        }
        public async Task OpenAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        {
            Opens++; events.Add("open"); if (RefuseOpen) throw new IOException("isolation refused");
            if (OpenGate is { } gate) { OpenEntered.TrySetResult(); await gate.Task; await inner.OpenAsync(points, Token); OpenFinished.TrySetResult(); }
            else await inner.OpenAsync(points, token);
        }
        public async Task CloseAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token) { Closes++; events.Add("close"); await inner.CloseAsync(points, token); if (AfterClose is not null) await AfterClose(); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public async Task ChangeExternallyAsync() => await inner.OpenAsync([PathA], Token);
    }
    private sealed class ProbeSource : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new(new("dc"));
        public bool FailConfigure { get; set; }
        public bool FailStop { get; set; }
        public int Connections { get; private set; }
        public InstrumentId Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) { Connections++; return inner.ConnectAsync(token); }
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint source, CancellationToken token) => FailConfigure ? Task.FromException(new IOException("DC preparation failed")) : inner.ConfigureSourceAsync(source, token);
        public Task ConfigureSenseAsync(SenseConfiguration sense, CancellationToken token) => inner.ConfigureSenseAsync(sense, token);
        public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token);
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token) => inner.ReadSourceSenseAsync(token);
        public Task SafeStopAsync(CancellationToken token) => FailStop ? Task.FromException(new IOException("DC stop refused")) : inner.SafeStopAsync(token);
    }
}

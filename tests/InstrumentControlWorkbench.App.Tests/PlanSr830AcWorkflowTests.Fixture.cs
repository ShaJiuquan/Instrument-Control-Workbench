using InstrumentControlWorkbench.App.ViewModels;
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
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanSr830AcWorkflowTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private const string Palette = "SR830 主动交流原始帧";
    private static readonly MatrixCrossPoint First = new(1, 1, 1);
    private static readonly MatrixCrossPoint Second = new(1, 1, 2);
    private static readonly MatrixCrossPoint Unrelated = new(1, 2, 2);
    private static ReadSr830AcFrameStep Read(Bench b, IEnumerable<MatrixCrossPoint>? paths = null) => new(new("ac"), b.Generator.Descriptor.Id, b.Matrix.Definition.Device.Id, paths ?? [Second, First], new(new(17, PhysicalUnit.Hertz), new(.01, PhysicalUnit.Volt), 2), Sr830InputMode.VoltageAB, new("raw"), TimeSpan.FromTicks(1234));
    private static void Fill(PlanSr830AcFormViewModel form)
    {
        form.SelectedGenerator = Assert.Single(form.Generators); form.SelectedIsolator = Assert.Single(form.Isolators); form.SelectedInput = Sr830InputMode.VoltageAB;
        form.Frequency = "17"; form.SineAmplitude = ".01"; form.Harmonic = "2"; form.Prefix = "ui_ac"; form.SettleSeconds = "0";
    }
    private sealed class Bench
    {
        public GeneratorPort Generator { get; } = new();
        public MatrixPort Matrix { get; }
        public PassivePort? Passive { get; }
        public MatrixPort? PassiveMatrix { get; }
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public int IoCalls => Generator.IoCalls + Matrix.IoCalls + (Passive?.IoCalls ?? 0) + (PassiveMatrix?.IoCalls ?? 0);
        public Bench(bool withRead = false, bool withRepeat = false, bool occupiedColumn = false, bool mixedCatalog = false, IRunResultListener? listener = null)
        {
            var hub = new NotificationHub(); Matrix = new(Definition(new("isolator"), Generator.Descriptor.Id));
            var sessions = new List<IDeviceSession> { new Sr830MeterSession(Generator, hub), new MatrixSwitchSession(Matrix, hub) };
            if (mixedCatalog) { Passive = new(); PassiveMatrix = new(Definition(new("passive-isolator"), Passive.Descriptor.Id)); sessions.Add(new Sr830MeterSession(Passive, hub)); sessions.Add(new MatrixSwitchSession(PassiveMatrix, hub)); }
            var registry = new DeviceRegistry(sessions, Leases); Runs = new(registry, hub, listener is null ? null : [listener]); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var steps = new List<ExperimentStep>(); if (withRead) steps.Add(Read(this));
            if (withRepeat) steps.Add(new RepeatStep(new("repeat"), 3, new SequenceStep(new("repeat-body"), [])));
            if (occupiedColumn) steps.Add(new ReadStep(new("scalar"), [new(new("occupied.x_v"), new(Generator.Descriptor.Id, Sr830Parameters.X.Id))]));
            Shell = new(null, Runs, hub, Workspace, new("Active SR830 authoring", new SequenceStep(new("root"), steps)), "unused-sr830-active.json");
        }
        private static MatrixSwitchDefinition Definition(InstrumentId id, InstrumentId generator) => new(new(id, "MATRIX", "Registered isolation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 4)], [], new(generator, [First, Second], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)));
        public void AssertReleased()
        {
            Assert.False(Runs.HasActiveRun); Assert.False(Leases.IsLeased(Generator.Descriptor.Id, DateTimeOffset.UtcNow)); Assert.False(Leases.IsLeased(Matrix.Definition.Device.Id, DateTimeOffset.UtcNow));
        }
    }
    private sealed class GeneratorPort : ISr830ExcitationPort
    {
        private readonly MockSr830ExcitationMeter inner = new(new("active-sr830"), "Active simulator");
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public int IoCalls { get; private set; }
        public TaskCompletionSource? ReadGate { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token) { IoCalls++; return inner.ConnectAsync(token); }
        public Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token) { IoCalls++; return inner.GetSnapshotAsync(token); }
        public Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token) { IoCalls++; return inner.ConfigureAsync(settings, token); }
        public Task ConfigureExcitationAsync(Sr830ExcitationSettings settings, CancellationToken token) { IoCalls++; return inner.ConfigureExcitationAsync(settings, token); }
        public async Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token) { IoCalls++; ReadEntered.TrySetResult(); if (ReadGate is { } gate) await gate.Task.WaitAsync(token); return await inner.ReadFrameAsync(token); }
        public Task AbortAcquisitionAsync(CancellationToken token) { IoCalls++; return inner.AbortAcquisitionAsync(token); }
        public Task DisconnectAsync(CancellationToken token) { IoCalls++; return inner.DisconnectAsync(token); }
    }
    private sealed class PassivePort : ISr830MeterPort
    {
        private readonly MockSr830Meter inner = new(new("passive-sr830"), "Passive reference-capable simulator");
        public DeviceDescriptor Descriptor => inner.Descriptor;
        public int IoCalls { get; private set; }
        public Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token) { IoCalls++; return inner.ConnectAsync(token); }
        public Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token) { IoCalls++; return inner.GetSnapshotAsync(token); }
        public Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token) { IoCalls++; return inner.ConfigureAsync(settings, token); }
        public Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token) { IoCalls++; return inner.ReadFrameAsync(token); }
        public Task AbortAcquisitionAsync(CancellationToken token) { IoCalls++; return inner.AbortAcquisitionAsync(token); }
        public Task DisconnectAsync(CancellationToken token) { IoCalls++; return inner.DisconnectAsync(token); }
    }
    private sealed class MatrixPort(MatrixSwitchDefinition definition) : IMatrixSwitchPort
    {
        private readonly MockMatrixSwitch inner = new(definition, [First, Second, Unrelated]);
        public MatrixSwitchDefinition Definition => definition;
        public int IoCalls { get; private set; }
        public Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) { IoCalls++; return inner.ConnectAsync(token); }
        public Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token) { IoCalls++; return inner.GetSnapshotAsync(token); }
        public Task OpenAsync(IReadOnlyList<MatrixCrossPoint> paths, CancellationToken token) { IoCalls++; return inner.OpenAsync(paths, token); }
        public Task CloseAsync(IReadOnlyList<MatrixCrossPoint> paths, CancellationToken token) { IoCalls++; return inner.CloseAsync(paths, token); }
        public Task DisconnectAsync(CancellationToken token) { IoCalls++; return inner.DisconnectAsync(token); }
    }
}

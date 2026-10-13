using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class Sr830AcCatalogTests
{
    private static readonly MatrixCrossPoint PathA = new(1, 1, 1);
    private static readonly MatrixCrossPoint PathB = new(1, 1, 2);
    private static MatrixSwitchDefinition Matrix(string id, string? generator = null) => new(
        new(new(id), "MATRIX", id, DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []),
        [new(1, 2, 4)], generator is null ? [new("dc-source")] : [],
        generator is null ? null : new MatrixAcIsolationCapability(new(generator), [PathA, PathB], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)));

    [Fact]
    public void CatalogPreservesMatrixRegistrationOrderAndContainsOnlyCompleteActiveBindings()
    {
        var hub = new NotificationHub();
        var zGenerator = new NoIoExcitationPort("z-generator");
        var aGenerator = new NoIoExcitationPort("a-generator");
        var legacy = new NoIoMatrixPort(Matrix("legacy"));
        var zMatrix = new NoIoMatrixPort(Matrix("z-isolator", "z-generator"));
        var aMatrix = new NoIoMatrixPort(Matrix("a-isolator", "a-generator"));
        var registry = new DeviceRegistry([
            new MatrixSwitchSession(zMatrix, hub), new Sr830MeterSession(aGenerator, hub),
            new MatrixSwitchSession(legacy, hub), new MatrixSwitchSession(aMatrix, hub),
            new Sr830MeterSession(zGenerator, hub)
        ], new(TimeSpan.FromSeconds(5)));

        Assert.Equal(new[] { zMatrix.Definition, aMatrix.Definition }, registry.Sr830AcIsolationDefinitions);
        Assert.Equal(new[] { zMatrix.Definition, legacy.Definition, aMatrix.Definition }, registry.MatrixDefinitions);
        Assert.All(registry.Sr830AcIsolationDefinitions, definition =>
        {
            var capability = Assert.IsType<MatrixAcIsolationCapability>(definition.AcIsolation);
            Assert.True(registry.SupportsSr830Excitation(capability.Generator));
            Assert.Equal(new[] { PathA, PathB }, capability.Connections);
            definition.Validate(capability.Connections);
        });
        Assert.Equal(0, zGenerator.IoCalls); Assert.Equal(0, aGenerator.IoCalls);
        Assert.Equal(0, zMatrix.IoCalls); Assert.Equal(0, aMatrix.IoCalls); Assert.Equal(0, legacy.IoCalls);
    }

    [Theory]
    [InlineData("passive")]
    [InlineData("no-reference")]
    [InlineData("missing")]
    [InlineData("mismatched")]
    [InlineData("wrong-session")]
    public void PassiveIncompatibleMissingAndMismatchedGeneratorBindingsAreNotAuthorable(string damage)
    {
        var hub = new NotificationHub();
        var matrix = new NoIoMatrixPort(Matrix("isolator", "generator"));
        var ports = new List<NoIoMeterPort>();
        var sessions = new List<IDeviceSession> { new MatrixSwitchSession(matrix, hub) };
        if (damage == "wrong-session")
            sessions.Add(new MatrixSwitchSession(new NoIoMatrixPort(Matrix("generator")), hub));
        else if (damage != "missing")
        {
            NoIoMeterPort generator = damage == "passive" ? new NoIoMeterPort("generator")
                : new NoIoExcitationPort(damage == "mismatched" ? "different-generator" : "generator", withReference: damage != "no-reference");
            ports.Add(generator); sessions.Add(new Sr830MeterSession(generator, hub));
        }
        var registry = new DeviceRegistry(sessions, new(TimeSpan.FromSeconds(5)));

        Assert.Empty(registry.Sr830AcIsolationDefinitions);
        Assert.Contains(matrix.Definition, registry.MatrixDefinitions);
        Assert.Equal(0, matrix.IoCalls);
        Assert.All(ports, port => Assert.Equal(0, port.IoCalls));
    }

    [Fact]
    public void AuthoringBoundaryReusesTheSameImmutableCatalogWithoutAnyDeviceIo()
    {
        var hub = new NotificationHub();
        var generator = new NoIoExcitationPort("generator");
        var matrix = new NoIoMatrixPort(Matrix("isolator", "generator"));
        var inputs = new List<IDeviceSession> { new Sr830MeterSession(generator, hub), new MatrixSwitchSession(matrix, hub) };
        var registry = new DeviceRegistry(inputs, new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub);
        var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
        inputs.Clear();

        Assert.Same(registry.Sr830AcIsolationDefinitions, runs.Sr830AcIsolationDefinitions);
        Assert.Same(registry.Sr830AcIsolationDefinitions, workspace.Sr830AcIsolationDefinitions);
        for (var i = 0; i < 3; i++) Assert.Same(matrix.Definition, Assert.Single(workspace.Sr830AcIsolationDefinitions));
        var list = Assert.IsAssignableFrom<IList<MatrixSwitchDefinition>>(workspace.Sr830AcIsolationDefinitions);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list[0] = matrix.Definition);
        var connections = Assert.IsAssignableFrom<IList<MatrixCrossPoint>>(matrix.Definition.AcIsolation!.Connections);
        Assert.Throws<NotSupportedException>(() => connections.Clear());
        Assert.Same(registry.MatrixDefinitions, workspace.MatrixDefinitions);
        Assert.Equal(0, generator.IoCalls); Assert.Equal(0, matrix.IoCalls);
        Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public void EmptyAndLegacyRegistriesExposeEmptyActiveCatalogs()
    {
        var hub = new NotificationHub();
        var empty = new DeviceRegistry([], new(TimeSpan.FromSeconds(5)));
        Assert.Empty(new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), new(empty, hub)).Sr830AcIsolationDefinitions);
        var legacy = new NoIoMatrixPort(Matrix("legacy"));
        var registry = new DeviceRegistry([new MatrixSwitchSession(legacy, hub)], new(TimeSpan.FromSeconds(5)));
        Assert.Empty(registry.Sr830AcIsolationDefinitions);
        Assert.Same(legacy.Definition, Assert.Single(registry.MatrixDefinitions));
        Assert.Equal(0, legacy.IoCalls);
    }

    // Every IO entry point fails. Metadata must be resolved without opening devices.
    private class NoIoMeterPort(string id, bool withReference = true) : ISr830MeterPort
    {
        public DeviceDescriptor Descriptor { get; } = new(new(id), "SR830", id, DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, withReference ? Sr830Parameters.WithReference : Sr830Parameters.All);
        public int IoCalls { get; private set; }
        protected InvalidOperationException UnexpectedIo() { IoCalls++; return new("AC authoring catalog must not perform device IO."); }
        public Task<Sr830MeterSnapshot> ConnectAsync(CancellationToken token) => throw UnexpectedIo();
        public Task<Sr830MeterSnapshot> GetSnapshotAsync(CancellationToken token) => throw UnexpectedIo();
        public Task ConfigureAsync(Sr830InputSettings settings, CancellationToken token) => throw UnexpectedIo();
        public Task<Sr830RawFrame> ReadFrameAsync(CancellationToken token) => throw UnexpectedIo();
        public Task AbortAcquisitionAsync(CancellationToken token) => throw UnexpectedIo();
        public Task DisconnectAsync(CancellationToken token) => throw UnexpectedIo();
    }
    private sealed class NoIoExcitationPort(string id, bool withReference = true) : NoIoMeterPort(id, withReference), ISr830ExcitationPort
    {
        public Task ConfigureExcitationAsync(Sr830ExcitationSettings settings, CancellationToken token) => throw UnexpectedIo();
    }
    private sealed class NoIoMatrixPort(MatrixSwitchDefinition definition) : IMatrixSwitchPort
    {
        public MatrixSwitchDefinition Definition { get; } = definition;
        public int IoCalls { get; private set; }
        private InvalidOperationException UnexpectedIo() { IoCalls++; return new("AC authoring catalog must not perform matrix IO."); }
        public Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) => throw UnexpectedIo();
        public Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token) => throw UnexpectedIo();
        public Task DisconnectAsync(CancellationToken token) => throw UnexpectedIo();
        public Task OpenAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token) => throw UnexpectedIo();
        public Task CloseAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token) => throw UnexpectedIo();
    }
}

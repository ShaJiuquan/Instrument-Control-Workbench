using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class TemperatureScanMetadataTests
{
    [Fact]
    public void ScanMetadataFiltersCapabilitiesAndPreservesRegistrationOrderOfBothAxisLists()
    {
        var hub = new NotificationHub();
        var zTemperature = new NoIoIndependentTemperaturePort(MockEnvironmentCatalog.Temperature(id: new("z-temperature")).Definition);
        var field = new NoIoAxisPort(MockEnvironmentCatalog.Field().Definition);
        var legacy = new NoIoLegacyTemperaturePort(MockEnvironmentCatalog.Temperature(id: new("legacy-temperature")).Definition);
        var angle = new NoIoAxisPort(MockEnvironmentCatalog.Angle().Definition);
        var aTemperature = new NoIoIndependentTemperaturePort(MockEnvironmentCatalog.PassiveTemperature(id: new("a-temperature")).Definition);
        var ports = new NoIoAxisPort[] { zTemperature, field, legacy, angle, aTemperature };
        var registry = new DeviceRegistry(ports.Select(port => new AxisControllerSession(port, hub)), new(TimeSpan.FromSeconds(5)));

        Assert.Equal(new[] { zTemperature.Definition, aTemperature.Definition }, registry.TemperatureScanDefinitions);
        Assert.Equal(ports.Select(port => port.Definition), registry.AxisDefinitions);
        Assert.Equal(ports.Select(port => port.Definition.Device), registry.Devices);
        Assert.All(ports, port => Assert.Equal(0, port.IoCalls));
    }

    [Fact]
    public void KelvinUnitsAloneDoNotAdvertiseIndependentScanConfiguration()
    {
        var hub = new NotificationHub();
        var ordinaryProfile = MockEnvironmentCatalog.Temperature(id: new("ordinary-kelvin")).Definition;
        var ordinaryKelvin = new NoIoAxisPort(new(ordinaryProfile.Device, ordinaryProfile.Target, ordinaryProfile.Readback, ordinaryProfile.Rate));
        var legacy = new NoIoLegacyTemperaturePort(MockEnvironmentCatalog.Temperature(id: new("legacy-kelvin")).Definition);
        var registry = new DeviceRegistry([new AxisControllerSession(ordinaryKelvin, hub), new AxisControllerSession(legacy, hub)], new(TimeSpan.FromSeconds(5)));

        Assert.Empty(registry.TemperatureScanDefinitions);
        Assert.Equal(2, registry.AxisDefinitions.Count);
        Assert.False(registry.SupportsTemperatureConfiguration(ordinaryKelvin.Definition.Device.Id));
        Assert.False(registry.SupportsTemperatureConfiguration(legacy.Definition.Device.Id));
        Assert.Equal(0, ordinaryKelvin.IoCalls);
        Assert.Equal(0, legacy.IoCalls);
    }

    [Fact]
    public void ExistingSimulatedTemperatureControllersAdvertiseBothScanModes()
    {
        var hub = new NotificationHub();
        var temperature = MockEnvironmentCatalog.Temperature(id: new("temperature"));
        var cryostat = MockEnvironmentCatalog.PassiveTemperature(id: new("cryostat"));
        var field = MockEnvironmentCatalog.Field();
        var angle = MockEnvironmentCatalog.Angle();
        var registry = new DeviceRegistry([
            new AxisControllerSession(temperature, hub), new AxisControllerSession(field, hub),
            new AxisControllerSession(cryostat, hub), new AxisControllerSession(angle, hub)
        ], new(TimeSpan.FromSeconds(5)));

        Assert.Equal(new[] { temperature.Definition, cryostat.Definition }, registry.TemperatureScanDefinitions);
        Assert.All(registry.TemperatureScanDefinitions, definition =>
        {
            Assert.True(registry.SupportsTemperatureConfiguration(definition.Device.Id));
            Assert.True(definition.SupportsSoftwareTemperatureRamp);
            Assert.Equal(PhysicalUnit.Kelvin, definition.Target.Unit);
            Assert.Equal(PhysicalUnit.Kelvin, definition.Readback.Unit);
            Assert.Equal(PhysicalUnit.KelvinPerMinute, definition.Rate.Unit);
        });
        Assert.Equal(4, registry.AxisDefinitions.Count);
    }

    [Fact]
    public void AuthoringQueriesReuseReadOnlyMetadataWithoutConnectingOrDoingAnyIo()
    {
        var hub = new NotificationHub();
        var port = new NoIoIndependentTemperaturePort(MockEnvironmentCatalog.Temperature().Definition);
        var registry = new DeviceRegistry([new AxisControllerSession(port, hub)], new InstrumentLeaseService(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub);
        var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);

        Assert.Same(registry.TemperatureScanDefinitions, runs.TemperatureScanDefinitions);
        Assert.Same(registry.TemperatureScanDefinitions, workspace.TemperatureScanDefinitions);
        Assert.Same(registry.AxisDefinitions, workspace.AxisDefinitions);
        for (var i = 0; i < 3; i++) Assert.Same(port.Definition, Assert.Single(workspace.TemperatureScanDefinitions));
        var list = Assert.IsAssignableFrom<IList<AxisControllerDefinition>>(workspace.TemperatureScanDefinitions);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list[0] = port.Definition);
        Assert.Equal(0, port.IoCalls);
        Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public void EmptyRegistryProvidesEmptyScanMetadataThroughAuthoringBoundary()
    {
        var hub = new NotificationHub();
        var registry = new DeviceRegistry([], new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub);
        var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);

        Assert.Empty(workspace.TemperatureScanDefinitions);
        Assert.Empty(workspace.AxisDefinitions);
        Assert.Same(registry.TemperatureScanDefinitions, workspace.TemperatureScanDefinitions);
    }

    // Every IO entry point fails: static authoring must rely on capability metadata only.
    private class NoIoAxisPort(AxisControllerDefinition definition) : IAxisControllerPort
    {
        public AxisControllerDefinition Definition { get; } = definition;
        public int IoCalls { get; private set; }
        protected InvalidOperationException UnexpectedIo() { IoCalls++; return new("Metadata authoring must not perform device IO."); }
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => throw UnexpectedIo();
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => throw UnexpectedIo();
        public Task SetTargetAsync(Quantity value, CancellationToken token) => throw UnexpectedIo();
        public Task SetRateAsync(Quantity value, CancellationToken token) => throw UnexpectedIo();
        public Task HoldAsync(CancellationToken token) => throw UnexpectedIo();
        public Task DisconnectAsync(CancellationToken token) => throw UnexpectedIo();
    }

    private class NoIoLegacyTemperaturePort(AxisControllerDefinition definition) : NoIoAxisPort(definition), ITemperatureControlPort
    {
        public Task PrepareSoftwareRampAsync(Quantity value, CancellationToken token) => throw UnexpectedIo();
        public Task SetHardwareRampAsync(Quantity value, CancellationToken token) => throw UnexpectedIo();
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token) => throw UnexpectedIo();
    }

    private sealed class NoIoIndependentTemperaturePort(AxisControllerDefinition definition) : NoIoLegacyTemperaturePort(definition), ITemperatureConfigurationPort
    {
        public Task ConfigureControlAsync(TemperatureControlConfiguration value, Quantity? rate, CancellationToken token) => throw UnexpectedIo();
    }
}

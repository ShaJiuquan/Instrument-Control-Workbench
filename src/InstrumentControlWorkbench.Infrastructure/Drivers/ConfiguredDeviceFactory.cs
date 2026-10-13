using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Infrastructure.Drivers;

// Hardware settings are retained by the document but never impersonated by mocks.
public sealed class ConfiguredDeviceFactory(TimeProvider? clock = null) : IConfiguredDeviceFactory
{
    public void Validate(DeviceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Connection is TcpDeviceConnection tcp && configuration.Category == ConfiguredDeviceCategory.SourceMeter && configuration.Model.Equals("K2450", StringComparison.OrdinalIgnoreCase))
        {
            if (tcp.Terminator is not ("\n" or "\r\n" or "\\n" or "\\r\\n")) throw new NotSupportedException("K2450 TCP requires LF or CRLF command termination.");
            return;
        }
        if (configuration.Connection is not SimulationDeviceConnection)
            throw new NotSupportedException($"{configuration.Id}: no hardware adapter is registered for {configuration.Model} and {configuration.Connection.GetType().Name}.");
        var supported = (configuration.Category, configuration.Model.ToUpperInvariant()) switch
        {
            (ConfiguredDeviceCategory.SourceMeter, "K2450") => true,
            (ConfiguredDeviceCategory.Source, "K6221") => true,
            (ConfiguredDeviceCategory.Meter, "K2182") => true,
            (ConfiguredDeviceCategory.LockIn, "SR830") => true,
            (ConfiguredDeviceCategory.Matrix, "K3706") => configuration.Matrix is not null,
            (ConfiguredDeviceCategory.Temperature, "SIM-TEMPERATURE") => true,
            (ConfiguredDeviceCategory.Field, "SIM-FIELD") => true,
            (ConfiguredDeviceCategory.Rotation, "ROTATOR") => true,
            _ => false,
        };
        if (!supported) throw new NotSupportedException($"{configuration.Id}: model/category is unsupported or matrix cards/guards are missing.");
    }
    public ConfiguredDevicePorts Create(DeviceConfiguration configuration)
    {
        Validate(configuration);
        if (configuration.Connection is TcpDeviceConnection tcp)
            return new(new TcpKeithley2450SourceMeter(configuration.Id, configuration.DisplayName, tcp));
        return configuration.Category switch
        {
            ConfiguredDeviceCategory.SourceMeter => new(new MockKeithley2450SourceMeter(configuration.Id, configuration.DisplayName)),
            ConfiguredDeviceCategory.Source => new(new MockKeithley6221CurrentSource(configuration.Id, configuration.DisplayName)),
            ConfiguredDeviceCategory.LockIn => new(new MockSr830Meter(configuration.Id, configuration.DisplayName)),
            ConfiguredDeviceCategory.Meter => new(new MockKeithley2182Meter(configuration.Id, configuration.DisplayName)),
            ConfiguredDeviceCategory.Temperature => new(MockEnvironmentCatalog.Temperature(clock, configuration.Id, configuration.DisplayName)),
            ConfiguredDeviceCategory.Field => new(MockEnvironmentCatalog.Field(clock, configuration.Id, configuration.DisplayName)),
            ConfiguredDeviceCategory.Rotation => new(MockEnvironmentCatalog.Angle(clock, configuration.Id, configuration.DisplayName, supportsHoming: true)),
            ConfiguredDeviceCategory.Matrix => new(new MockMatrixSwitch(new(
                new(configuration.Id, configuration.Model, configuration.DisplayName, DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []),
                configuration.Matrix!.Cards, configuration.Matrix.GuardedSources))),
            _ => throw new NotSupportedException(),
        };
    }
}

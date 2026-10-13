using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Devices;

// Startup construction only. Saving/editing settings never mutates this runtime.
public sealed class ConfiguredDeviceRuntime
{
    public ConfiguredDeviceRuntime(DeviceConfigurationCatalog configuration, IConfiguredDeviceFactory factory,
        InstrumentLeaseService leases, NotificationHub notifications)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(leases); ArgumentNullException.ThrowIfNull(notifications);
        var enabled = configuration.Devices.Where(d => d.Enabled).ToArray();
        foreach (var device in enabled) factory.Validate(device);
        var sessions = new List<IDeviceSession>(); var sources = new Dictionary<InstrumentId, InstrumentManager>();
        foreach (var device in enabled)
        {
            var ports = factory.Create(device);
            if (ports.Descriptor.Id != device.Id || ports.Descriptor.DriverKind != device.DriverKind || ports.Descriptor.DisplayName != device.DisplayName ||
                ports.SourceMeter is not null && device.Category != ConfiguredDeviceCategory.SourceMeter ||
                ports.CurrentSource is not null && device.Category != ConfiguredDeviceCategory.Source ||
                ports.Picoammeter is not null && device.Category != ConfiguredDeviceCategory.Meter ||
                ports.Meter is not null && device.Category != ConfiguredDeviceCategory.Meter ||
                ports.LockIn is not null && device.Category != ConfiguredDeviceCategory.LockIn ||
                ports.Matrix is not null && device.Category != ConfiguredDeviceCategory.Matrix ||
                ports.Axis is not null && device.Category is not (ConfiguredDeviceCategory.Temperature or ConfiguredDeviceCategory.Field or ConfiguredDeviceCategory.Rotation))
                throw new ArgumentException($"{device.Id}: factory identity, driver kind or execution category does not match configuration.");
            if (ports.SourceMeter is { } source)
            {
                var manager = new InstrumentManager(source, notifications); sources.Add(device.Id, manager); sessions.Add(manager);
            }
            else if (ports.Meter is { } meter) sessions.Add(new Keithley2182MeterSession(meter, notifications));
            else if (ports.Picoammeter is { } picoammeter) sessions.Add(new Keithley6485MeterSession(picoammeter, notifications));
            else if (ports.LockIn is { } lockIn) sessions.Add(new Sr830MeterSession(lockIn, notifications));
            else if (ports.CurrentSource is { } current) sessions.Add(new Keithley6221CurrentSourceSession(current, notifications));
            else if (ports.Axis is { } axis)
            {
                var expectedUnit = device.Category switch { ConfiguredDeviceCategory.Temperature => PhysicalUnit.Kelvin, ConfiguredDeviceCategory.Field => PhysicalUnit.Tesla, _ => PhysicalUnit.Degree };
                if (axis.Definition.Target.Unit != expectedUnit) throw new ArgumentException($"{device.Id}: factory axis units do not match its category.");
                sessions.Add(new AxisControllerSession(axis, notifications));
            }
            else
            {
                var matrix = ports.Matrix!;
                if (device.Matrix is null || !matrix.Definition.Cards.SequenceEqual(device.Matrix.Cards) || !matrix.Definition.GuardedSources.SequenceEqual(device.Matrix.GuardedSources))
                    throw new ArgumentException($"{device.Id}: factory matrix cards or guarded sources do not match configuration.");
                sessions.Add(new MatrixSwitchSession(matrix, notifications));
            }
        }
        Registry = new(sessions, leases); Configuration = configuration;
        Sources = new ReadOnlyDictionary<InstrumentId, InstrumentManager>(sources);
    }
    public DeviceConfigurationCatalog Configuration { get; }
    public DeviceRegistry Registry { get; }
    public IReadOnlyDictionary<InstrumentId, InstrumentManager> Sources { get; }
    public InstrumentManager PrimarySource => Configuration.PrimarySource is { } selected ? Sources[selected]
        : throw new InvalidOperationException("No primary source meter is selected; this runtime can manage its other devices independently.");
    public ExperimentImportBindings CreateImportBindings(IEnumerable<SourcePreparation>? preparations = null, IEnumerable<CurrentSourcePreparation>? currentSourcePreparations = null)
    {
        var registered = Registry.Devices.Select(d => d.Id).ToHashSet();
        var aliases = Configuration.Aliases.Where(p => registered.Contains(p.Value)).ToArray();
        var axes = aliases.Where(p => p.Key.Equals("temperature", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("field", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("angle", StringComparison.OrdinalIgnoreCase))
            .Select(p => KeyValuePair.Create(p.Key, Registry.GetAxisDefinition(p.Value)));
        return new(aliases, axes, preparations, standaloneVoltageMeters: Configuration.Devices.Where(d => d.Enabled && aliases.Any(a => a.Value == d.Id) && d.Category == ConfiguredDeviceCategory.Meter && d.Model.Equals("K2182", StringComparison.OrdinalIgnoreCase)).Select(d => d.Id), currentSourcePreparations: currentSourcePreparations, standaloneCurrentMeters: Configuration.Devices.Where(d => d.Enabled && aliases.Any(a => a.Value == d.Id) && d.Category == ConfiguredDeviceCategory.Meter && d.Model.Equals("K6485", StringComparison.OrdinalIgnoreCase)).Select(d => d.Id));
    }
}

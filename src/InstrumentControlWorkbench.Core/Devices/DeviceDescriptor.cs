using System.Collections.ObjectModel;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Devices;

public enum DriverKind { Simulation, Hardware }
public enum DeviceStopPolicy { OutputOff, HoldCurrentPosition, OpenRunAddedConnections, AbortAcquisition }

public sealed record DeviceDescriptor
{
    public DeviceDescriptor(InstrumentId id, string model, string displayName, DriverKind driverKind,
        DeviceStopPolicy stopPolicy, IEnumerable<ParameterDescriptor> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!Enum.IsDefined(driverKind)) throw new ArgumentOutOfRangeException(nameof(driverKind));
        if (!Enum.IsDefined(stopPolicy)) throw new ArgumentOutOfRangeException(nameof(stopPolicy));
        ArgumentNullException.ThrowIfNull(parameters);
        var items = parameters.ToArray();
        if (items.Any(p => p is null) || items.Select(p => p.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("Device parameters must be non-null and unique.", nameof(parameters));
        Id = id;
        Model = model;
        DisplayName = displayName;
        DriverKind = driverKind;
        StopPolicy = stopPolicy;
        Parameters = Array.AsReadOnly(items);
    }

    public InstrumentId Id { get; }
    public string Model { get; }
    public string DisplayName { get; }
    public DriverKind DriverKind { get; }
    public DeviceStopPolicy StopPolicy { get; }
    public IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public ParameterDescriptor GetParameter(ParameterId id) => Parameters.SingleOrDefault(p => p.Id == id)
        ?? throw new NotSupportedException($"{Id} does not support parameter {id}.");
}

public sealed record DeviceStatus
{
    public DeviceStatus(DeviceDescriptor descriptor, InstrumentLifecycleState lifecycle, bool connected,
        bool safetyConfirmed, DateTimeOffset timestamp, IReadOnlyDictionary<ParameterId, Quantity> values, TemperatureRuntimeStatus? temperature = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(lifecycle)) throw new ArgumentOutOfRangeException(nameof(lifecycle));
        if (safetyConfirmed && !connected && descriptor.DriverKind == DriverKind.Hardware) throw new ArgumentException("A disconnected observation cannot confirm the hardware stop policy.", nameof(safetyConfirmed));
        foreach (var value in values) descriptor.GetParameter(value.Key).Validate(value.Value);
        if (temperature is not null && (!descriptor.Parameters.Any(p => p.Unit == PhysicalUnit.Kelvin && p.Access.HasFlag(ParameterAccess.Read)) || !connected && temperature.State != TemperatureStabilityState.Fault))
            throw new ArgumentException("Temperature runtime requires a readable Kelvin profile and fault state when disconnected.", nameof(temperature));
        Descriptor = descriptor;
        Lifecycle = lifecycle;
        Connected = connected;
        SafetyConfirmed = safetyConfirmed;
        Timestamp = timestamp;
        Values = new ReadOnlyDictionary<ParameterId, Quantity>(new Dictionary<ParameterId, Quantity>(values));
        Temperature = temperature;
    }

    public DeviceDescriptor Descriptor { get; }
    public InstrumentLifecycleState Lifecycle { get; }
    public bool Connected { get; }
    public bool SafetyConfirmed { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> Values { get; }
    public TemperatureRuntimeStatus? Temperature { get; }
}

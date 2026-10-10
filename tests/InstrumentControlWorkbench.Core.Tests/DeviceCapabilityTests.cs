using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class DeviceCapabilityTests
{
    [Fact]
    public void CapabilitiesAndStatusCopyMutableInputCollections()
    {
        var parameter = new ParameterDescriptor(new("temperature.target"), "Target", PhysicalUnit.Kelvin,
            ParameterAccess.ReadWrite, new(1, PhysicalUnit.Kelvin), new(310, PhysicalUnit.Kelvin));
        var parameters = new List<ParameterDescriptor> { parameter };
        var descriptor = new DeviceDescriptor(new("temperature"), "SIM-TEMP", "Temperature", DriverKind.Simulation,
            DeviceStopPolicy.HoldCurrentPosition, parameters);
        parameters.Clear();
        var values = new Dictionary<ParameterId, Quantity> { [parameter.Id] = new(300, PhysicalUnit.Kelvin) };
        var status = new DeviceStatus(descriptor, InstrumentLifecycleState.Idle, true, true, DateTimeOffset.UnixEpoch, values);
        values[parameter.Id] = new(310, PhysicalUnit.Kelvin);
        Assert.Single(descriptor.Parameters);
        Assert.Equal(300, status.Values[parameter.Id].Value);
    }

    [Fact]
    public void UnknownParametersAndWrongSnapshotUnitsCannotEnterTheBoundary()
    {
        var parameter = new ParameterDescriptor(new("temperature"), "Temperature", PhysicalUnit.Kelvin, ParameterAccess.Read);
        var descriptor = new DeviceDescriptor(new("temp"), "SIM", "Temperature", DriverKind.Simulation,
            DeviceStopPolicy.HoldCurrentPosition, [parameter]);
        Assert.Throws<NotSupportedException>(() => descriptor.GetParameter(new("missing")));
        Assert.Throws<ArgumentException>(() => new DeviceStatus(descriptor, InstrumentLifecycleState.Idle, true, true,
            DateTimeOffset.UnixEpoch, new Dictionary<ParameterId, Quantity> { [parameter.Id] = new(300, PhysicalUnit.Volt) }));
    }

    [Fact]
    public void DisconnectedObservationCannotClaimConfirmedHardwareSafety()
    {
        var descriptor = new DeviceDescriptor(new("source"), "K2450", "Source", DriverKind.Hardware,
            DeviceStopPolicy.OutputOff, []);
        Assert.Throws<ArgumentException>(() => new DeviceStatus(descriptor, InstrumentLifecycleState.Disconnected,
            false, true, DateTimeOffset.UnixEpoch, new Dictionary<ParameterId, Quantity>()));
    }

    [Fact]
    public void DuplicateCapabilitiesAndEmptyIdentityAreRejected()
    {
        var parameter = new ParameterDescriptor(new("voltage"), "Voltage", PhysicalUnit.Volt, ParameterAccess.Read);
        Assert.Throws<ArgumentException>(() => new DeviceDescriptor(new("smu"), "K2450", "Source", DriverKind.Simulation,
            DeviceStopPolicy.OutputOff, [parameter, parameter]));
        Assert.ThrowsAny<ArgumentException>(() => new DeviceDescriptor(default, "K2450", "Source", DriverKind.Simulation,
            DeviceStopPolicy.OutputOff, [parameter]));
    }

    [Theory]
    [InlineData(PhysicalUnit.Kelvin, PhysicalUnit.KelvinPerMinute)]
    [InlineData(PhysicalUnit.Tesla, PhysicalUnit.TeslaPerSecond)]
    [InlineData(PhysicalUnit.Degree, PhysicalUnit.DegreePerSecond)]
    public void AxisRateMustMatchThePositionUnit(PhysicalUnit unit, PhysicalUnit rateUnit)
    {
        var target = new ParameterDescriptor(new("target"), "Target", unit, ParameterAccess.ReadWrite);
        var actual = new ParameterDescriptor(new("actual"), "Actual", unit, ParameterAccess.Read);
        var rate = new ParameterDescriptor(new("rate"), "Rate", rateUnit, ParameterAccess.ReadWrite, new(0.01, rateUnit));
        var descriptor = new DeviceDescriptor(new("axis"), "SIM", "Axis", DriverKind.Simulation,
            DeviceStopPolicy.HoldCurrentPosition, [target, actual, rate]);
        Assert.Equal(rate, new AxisControllerDefinition(descriptor, target, actual, rate).Rate);
        var wrong = new ParameterDescriptor(new("wrong.rate"), "Rate", PhysicalUnit.Second, ParameterAccess.ReadWrite, new(0.01, PhysicalUnit.Second));
        var wrongDevice = new DeviceDescriptor(new("axis"), "SIM", "Axis", DriverKind.Simulation,
            DeviceStopPolicy.HoldCurrentPosition, [target, actual, wrong]);
        Assert.Throws<ArgumentException>(() => new AxisControllerDefinition(wrongDevice, target, actual, wrong));
    }
}

using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class RotationHomingTests
{
    [Theory]
    [InlineData("unit")]
    [InlineData("zero")]
    [InlineData("poll")]
    public void HomeStepRejectsInvalidTimingAndAngleTolerance(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HomeRotationStep(new("home"), new("rotator"), new(damage == "zero" ? 0 : .5, damage == "unit" ? PhysicalUnit.Kelvin : PhysicalUnit.Degree), damage == "poll" ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500)));
    }
    [Theory]
    [InlineData(PhysicalUnit.Kelvin, 1d, 310d, PhysicalUnit.KelvinPerMinute)]
    [InlineData(PhysicalUnit.Degree, 1d, 360d, PhysicalUnit.DegreePerSecond)]
    public void AdvertisedHomingRequiresABoundedRotationRangeContainingZero(PhysicalUnit unit, double minimum, double maximum, PhysicalUnit rateUnit)
    {
        var target = new ParameterDescriptor(new("axis.target"), "Target", unit, ParameterAccess.ReadWrite, new(minimum, unit), new(maximum, unit)); var read = new ParameterDescriptor(new("axis.read"), "Readback", unit, ParameterAccess.Read, new(minimum, unit), new(maximum, unit)); var rate = new ParameterDescriptor(new("axis.rate"), "Rate", rateUnit, ParameterAccess.ReadWrite, new(.1, rateUnit), new(30, rateUnit));
        var device = new DeviceDescriptor(new("axis"), "SIM", "Axis", DriverKind.Simulation, DeviceStopPolicy.HoldCurrentPosition, [target, read, rate]); Assert.Throws<ArgumentException>(() => new AxisControllerDefinition(device, target, read, rate, supportsHoming: true));
    }
}

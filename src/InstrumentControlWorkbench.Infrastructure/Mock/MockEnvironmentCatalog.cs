using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.Infrastructure.Mock;

// These are simulation profiles, not certified hardware limits or protocols.
// Target ranges follow the captured PPMS schema; field slew is a demo setting.
public static class MockEnvironmentCatalog
{
    public static MockAxisController Temperature(TimeProvider? clock = null) => Create(new("temperature"), "Temperature simulator",
        "temperature", PhysicalUnit.Kelvin, 1, 310, PhysicalUnit.KelvinPerMinute, 0.01, 20, 300, 5, clock);
    public static MockAxisController Field(TimeProvider? clock = null) => Create(new("field"), "Magnetic-field simulator",
        "field", PhysicalUnit.Tesla, -9, 9, PhysicalUnit.TeslaPerSecond, 0.0001, 1, 0, 0.1, clock);
    public static MockAxisController Angle(TimeProvider? clock = null) => Create(new("rotator"), "Rotator simulator",
        "angle", PhysicalUnit.Degree, -360, 360, PhysicalUnit.DegreePerSecond, 0.1, 30, 0, 5, clock);

    private static MockAxisController Create(InstrumentId id, string name, string prefix, PhysicalUnit unit, double min, double max,
        PhysicalUnit rateUnit, double minRate, double maxRate, double initial, double initialRate, TimeProvider? clock)
    {
        var target = new ParameterDescriptor(new($"{prefix}.target"), $"{name} target", unit, ParameterAccess.ReadWrite, new(min, unit), new(max, unit));
        var readback = new ParameterDescriptor(new($"{prefix}.readback"), $"{name} readback", unit, ParameterAccess.Read, new(min, unit), new(max, unit));
        var rate = new ParameterDescriptor(new($"{prefix}.rate"), $"{name} rate", rateUnit, ParameterAccess.ReadWrite, new(minRate, rateUnit), new(maxRate, rateUnit));
        var device = new DeviceDescriptor(id, $"SIM-{prefix.ToUpperInvariant()}", name, DriverKind.Simulation,
            DeviceStopPolicy.HoldCurrentPosition, [target, readback, rate]);
        return new(new(device, target, readback, rate, unit == PhysicalUnit.Kelvin), new(initial, unit), new(initialRate, rateUnit), clock);
    }
}

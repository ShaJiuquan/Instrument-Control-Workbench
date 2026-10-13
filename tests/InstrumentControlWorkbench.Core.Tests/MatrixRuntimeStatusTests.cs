using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class MatrixRuntimeStatusTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static MatrixSwitchDefinition Definition() => new(new(new("matrix"), "MATRIX", "Isolation", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 2, 2)], [], new(new("generator"), [new(1, 1, 1)], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz)));
    [Fact]
    public void StatusRetainsImmutableOriginalMatrixReadback()
    {
        var definition = Definition(); var snapshot = new MatrixSwitchSnapshot(definition, true, true, [], T);
        var status = new DeviceStatus(definition.Device, InstrumentLifecycleState.Idle, true, true, T, new Dictionary<ParameterId, Quantity>(), matrix: snapshot);
        Assert.Same(snapshot, status.Matrix); Assert.Empty(status.Matrix!.Closed);
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("policy")]
    [InlineData("connected")]
    [InlineData("timestamp")]
    [InlineData("unknown-safe")]
    [InlineData("closed-safe")]
    [InlineData("default-time")]
    public void InconsistentProfileTimeRouteOrSafetyCannotCarryMatrixFact(string damage)
    {
        var definition = Definition(); var timestamp = damage == "default-time" ? default : T;
        var snapshot = new MatrixSwitchSnapshot(definition, true, damage != "unknown-safe", damage == "closed-safe" ? [new(1, 1, 1)] : [], timestamp);
        var descriptor = damage is "identity" or "policy" ? new DeviceDescriptor(new(damage == "identity" ? "other" : "matrix"), "MATRIX", "Other", DriverKind.Simulation, damage == "policy" ? DeviceStopPolicy.AbortAcquisition : DeviceStopPolicy.OpenRunAddedConnections, []) : definition.Device;
        Assert.Throws<ArgumentException>(() => new DeviceStatus(descriptor, InstrumentLifecycleState.Idle, damage != "connected", true, damage == "timestamp" ? T.AddSeconds(1) : timestamp, new Dictionary<ParameterId, Quantity>(), matrix: snapshot));
    }
}

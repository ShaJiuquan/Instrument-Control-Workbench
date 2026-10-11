using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class TemperatureRuntimeTests
{
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    [Fact]
    public void SoftwareRampUsesFinalIntentInsteadOfCurrentProgrammedSetpoint()
    {
        var observer = new TemperatureStabilityObserver();
        var snapshot = new AxisControllerSnapshot(true, K(300.05), K(300), new(5, PhysicalUnit.KelvinPerMinute), true, DateTimeOffset.UnixEpoch);
        var observation = new TemperatureStabilityObservation(snapshot, new(false, true), TemperatureStabilitySettings.Default, K(305));
        Assert.Equal(TemperatureStabilityState.Ramping, observer.Observe(observation, TimeSpan.Zero)); Assert.Null(observer.InToleranceSince);
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(observation with { SoftwareTarget = null }, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("error_unit")]
    [InlineData("negative_time")]
    [InlineData("unknown_control")]
    [InlineData("disabled_stable")]
    [InlineData("missing_error")]
    [InlineData("missing_rate")]
    [InlineData("rate_unit")]
    [InlineData("zero_rate")]
    [InlineData("cooling_with_error")]
    [InlineData("ramping_with_time")]
    public void RuntimeStatusRejectsContradictoryEvidence(string fault)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TemperatureRuntimeStatus(
            fault == "state" ? (TemperatureStabilityState)9 : fault == "cooling_with_error" ? TemperatureStabilityState.Cooling : fault == "ramping_with_time" ? TemperatureStabilityState.Ramping : TemperatureStabilityState.Stable,
            fault == "missing_error" ? null : fault == "error_unit" ? new(0, PhysicalUnit.Tesla) : K(0),
            fault == "unknown_control" ? null : new(false, fault is not ("disabled_stable" or "cooling_with_error")),
            TemperatureStabilitySettings.Default, fault == "negative_time" ? TimeSpan.FromSeconds(-1) : TimeSpan.Zero,
            fault is "missing_rate" or "rate_unit" or "zero_rate" ? K(305) : null,
            fault == "rate_unit" ? new(5, PhysicalUnit.Kelvin) : fault == "zero_rate" ? new(0, PhysicalUnit.KelvinPerMinute) : null));
    }
}

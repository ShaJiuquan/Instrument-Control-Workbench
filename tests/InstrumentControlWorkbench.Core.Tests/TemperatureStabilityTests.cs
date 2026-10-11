using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class TemperatureStabilityTests
{
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private static TemperatureStabilityObservation Observation(double readback = 300, double target = 300, bool connected = true, bool enabled = true, TemperatureStabilitySettings? settings = null)
        => new(new(connected, K(target), K(readback), new(5, PhysicalUnit.KelvinPerMinute), false, DateTimeOffset.UnixEpoch), new(false, enabled), settings ?? TemperatureStabilitySettings.Default);

    [Fact]
    public void ClassifiesFreshEvidenceAndRestartsAfterExcursionTargetAndSettingsChanges()
    {
        var observer = new TemperatureStabilityObserver();
        Assert.Equal(TemperatureStabilityState.Ramping, observer.Observe(Observation(295), TimeSpan.Zero));
        Assert.Equal(TemperatureStabilityState.Settling, observer.Observe(Observation(299), TimeSpan.FromSeconds(1)));
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(), TimeSpan.FromSeconds(2)));
        Assert.Equal(TemperatureStabilityState.Stable, observer.Observe(Observation(), TimeSpan.FromSeconds(7)));
        Assert.Equal(TemperatureStabilityState.Settling, observer.Observe(Observation(299), TimeSpan.FromSeconds(8)));
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(), TimeSpan.FromSeconds(9)));
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(301, 301), TimeSpan.FromSeconds(15)));
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(301, 301, settings: new(K(0.2), K(2), TimeSpan.FromSeconds(6))), TimeSpan.FromSeconds(21)));
    }

    [Theory]
    [InlineData(false, true, TemperatureStabilityState.Fault)]
    [InlineData(true, false, TemperatureStabilityState.Cooling)]
    public void FaultOrDisabledControlCannotReuseStableEvidence(bool connected, bool enabled, TemperatureStabilityState state)
    {
        var observer = new TemperatureStabilityObserver(); observer.Observe(Observation(), TimeSpan.Zero);
        Assert.Equal(TemperatureStabilityState.Stable, observer.Observe(Observation(), TimeSpan.FromSeconds(5)));
        Assert.Equal(state, observer.Observe(Observation(connected: connected, enabled: enabled), TimeSpan.FromSeconds(6)));
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void PauseResetAndClockRewindRequireFreshDuration()
    {
        var observer = new TemperatureStabilityObserver(); observer.Observe(Observation(), TimeSpan.FromSeconds(10));
        observer.Reset(); Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(), TimeSpan.FromSeconds(20)));
        Assert.Equal(TemperatureStabilityState.Reached, observer.Observe(Observation(), TimeSpan.FromSeconds(1)));
        Assert.Equal(TemperatureStabilityState.Stable, observer.Observe(Observation(), TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void ZeroThresholdDurationAndSettlingBeforeToleranceMatchDeclaredSemantics()
    {
        var observer = new TemperatureStabilityObserver();
        Assert.Equal(TemperatureStabilityState.Stable, observer.Observe(Observation(settings: new(K(0), K(0), TimeSpan.Zero)), TimeSpan.Zero));
        Assert.Equal(TemperatureStabilityState.Ramping, observer.Observe(Observation(299, settings: new(K(3), K(0.5), TimeSpan.Zero)), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("tolerance")]
    [InlineData("settling")]
    [InlineData("unit")]
    [InlineData("duration")]
    [InlineData("large_duration")]
    public void InvalidSettingsReject(string fault)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TemperatureStabilityChange(
            fault == "tolerance" ? K(-1) : fault == "unit" ? new(1, PhysicalUnit.Tesla) : null,
            fault == "settling" ? K(-1) : null,
            fault == "duration" ? TimeSpan.FromSeconds(-1) : fault == "large_duration" ? TimeSpan.MaxValue : null));
    }

    [Fact]
    public void PartialAndEmptyPatchesRetainIndependentFields()
    {
        var settings = new TemperatureStabilityChange(K(0.4)).Apply(TemperatureStabilitySettings.Default);
        Assert.Equal(K(0.4), settings.Tolerance); Assert.Equal(K(2), settings.SettlingThreshold); Assert.Equal(TimeSpan.FromSeconds(5), settings.StableFor);
        Assert.Equal(settings, new TemperatureStabilityChange().Apply(settings));
    }

    [Fact]
    public void WaitAndConfigurationContractsRejectInvalidIdsModesAndTimers()
    {
        var target = new ConstantValue(K(300));
        Assert.ThrowsAny<ArgumentException>(() => new TemperatureStabilityStep(new("config"), default, new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaitTemperatureStep(new("wait"), new("temperature"), target, (TemperatureWaitMode)9, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaitTemperatureStep(new("wait"), new("temperature"), target, TemperatureWaitMode.Stable, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }
}

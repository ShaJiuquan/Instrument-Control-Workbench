using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Devices;

public sealed record TemperatureStabilitySettings
{
    public TemperatureStabilitySettings(Quantity tolerance, Quantity settlingThreshold, TimeSpan stableFor)
    {
        ValidateThreshold(tolerance); ValidateThreshold(settlingThreshold);
        if (stableFor < TimeSpan.Zero || stableFor > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(stableFor));
        Tolerance = tolerance; SettlingThreshold = settlingThreshold; StableFor = stableFor;
    }
    public static TemperatureStabilitySettings Default { get; } = new(new(0.1, PhysicalUnit.Kelvin), new(2, PhysicalUnit.Kelvin), TimeSpan.FromSeconds(5));
    public Quantity Tolerance { get; }
    public Quantity SettlingThreshold { get; }
    public TimeSpan StableFor { get; }
    internal static void ValidateThreshold(Quantity value)
    {
        if (value.Unit != PhysicalUnit.Kelvin || value.Value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Temperature thresholds require non-negative Kelvin.");
    }
}

public sealed record TemperatureStabilityChange
{
    public TemperatureStabilityChange(Quantity? tolerance = null, Quantity? settlingThreshold = null, TimeSpan? stableFor = null)
    {
        // An empty patch is a no-op; omitted fields retain the session values.
        _ = new TemperatureStabilitySettings(tolerance ?? TemperatureStabilitySettings.Default.Tolerance,
            settlingThreshold ?? TemperatureStabilitySettings.Default.SettlingThreshold, stableFor ?? TemperatureStabilitySettings.Default.StableFor);
        Tolerance = tolerance; SettlingThreshold = settlingThreshold; StableFor = stableFor;
    }
    public Quantity? Tolerance { get; }
    public Quantity? SettlingThreshold { get; }
    public TimeSpan? StableFor { get; }
    public TemperatureStabilitySettings Apply(TemperatureStabilitySettings previous) => new(
        Tolerance ?? previous.Tolerance, SettlingThreshold ?? previous.SettlingThreshold, StableFor ?? previous.StableFor);
}

public enum TemperatureStabilityState { Fault, Cooling, Ramping, Settling, Reached, Stable }

public sealed record TemperatureStabilityObservation(AxisControllerSnapshot Snapshot, TemperatureControlConfiguration Control, TemperatureStabilitySettings Settings);

// Each explicit wait requires fresh uninterrupted evidence. Active elapsed time
// excludes operator pause; the executor resets this observer after every pause.
public sealed class TemperatureStabilityObserver
{
    private TimeSpan? since;
    private Quantity? previousTarget;
    private TemperatureStabilitySettings? previousSettings;
    public void Reset() { since = null; previousTarget = null; previousSettings = null; }
    public TemperatureStabilityState Observe(TemperatureStabilityObservation observation, TimeSpan activeElapsed)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (activeElapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activeElapsed));
        var (snapshot, control, settings) = observation;
        if (!snapshot.Connected) { Reset(); return TemperatureStabilityState.Fault; }
        if (!control.ControlEnabled) { Reset(); return TemperatureStabilityState.Cooling; }
        if (snapshot.Target.Unit != PhysicalUnit.Kelvin || snapshot.Readback.Unit != PhysicalUnit.Kelvin)
            throw new ArgumentException("Temperature observations require Kelvin.");
        if (snapshot.Target != previousTarget || settings != previousSettings) since = null;
        previousTarget = snapshot.Target; previousSettings = settings;
        var error = Math.Abs(snapshot.Target.Value - snapshot.Readback.Value);
        if (error > settings.SettlingThreshold.Value) { since = null; return TemperatureStabilityState.Ramping; }
        if (error > settings.Tolerance.Value) { since = null; return TemperatureStabilityState.Settling; }
        if (since is null || activeElapsed < since) since = activeElapsed;
        return activeElapsed - since.Value >= settings.StableFor ? TemperatureStabilityState.Stable : TemperatureStabilityState.Reached;
    }
}

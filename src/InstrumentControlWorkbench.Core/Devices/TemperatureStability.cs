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

public sealed record TemperatureStabilityObservation(AxisControllerSnapshot Snapshot, TemperatureControlConfiguration Control,
    TemperatureStabilitySettings Settings, Quantity? SoftwareTarget = null, TemperatureRuntimeStatus? Runtime = null);

public sealed record TemperatureRuntimeStatus
{
    public TemperatureRuntimeStatus(TemperatureStabilityState state, Quantity? error, TemperatureControlConfiguration? control,
        TemperatureStabilitySettings settings, TimeSpan? inToleranceFor, Quantity? softwareTarget, Quantity? softwareRate = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentNullException.ThrowIfNull(settings);
        if (error is { } e && e.Unit != PhysicalUnit.Kelvin || softwareTarget is { } t && t.Unit != PhysicalUnit.Kelvin)
            throw new ArgumentException("Temperature runtime values require Kelvin.");
        var tracking = state is TemperatureStabilityState.Ramping or TemperatureStabilityState.Settling or TemperatureStabilityState.Reached or TemperatureStabilityState.Stable;
        if (state != TemperatureStabilityState.Fault && control is null || tracking && (control?.ControlEnabled != true || error is null) || state == TemperatureStabilityState.Cooling && control?.ControlEnabled != false)
            throw new ArgumentException("Temperature runtime state requires matching observed control and error evidence.");
        if (!tracking && (error is not null || inToleranceFor is not null) || state is TemperatureStabilityState.Ramping or TemperatureStabilityState.Settling && inToleranceFor is not null)
            throw new ArgumentException("Only reached/stable states may carry in-tolerance duration.");
        if ((softwareTarget is null) != (softwareRate is null) || softwareRate is { } rate && (rate.Unit != PhysicalUnit.KelvinPerMinute || rate.Value <= 0))
            throw new ArgumentException("Active software ramp requires a target and positive Kelvin/minute rate together.");
        if (inToleranceFor < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inToleranceFor));
        State = state; Error = error; Control = control; Settings = settings; InToleranceFor = inToleranceFor; SoftwareTarget = softwareTarget; SoftwareRate = softwareRate;
    }
    public TemperatureStabilityState State { get; }
    public Quantity? Error { get; }
    public TemperatureControlConfiguration? Control { get; }
    public TemperatureStabilitySettings Settings { get; }
    public TimeSpan? InToleranceFor { get; }
    public Quantity? SoftwareTarget { get; }
    public Quantity? SoftwareRate { get; }
    public bool SoftwareRampActive => SoftwareTarget is not null;
}

// Pure shared state. The owning session supplies monotonic elapsed time and
// resets evidence at pause/stop, configuration changes and failed observations.
public sealed class TemperatureStabilityObserver
{
    private TimeSpan? since;
    private Quantity? previousTarget;
    private TemperatureStabilitySettings? previousSettings;
    public TimeSpan? InToleranceSince => since;
    public void Reset() { since = null; previousTarget = null; previousSettings = null; }
    public TemperatureStabilityState Observe(TemperatureStabilityObservation observation, TimeSpan activeElapsed)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (activeElapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activeElapsed));
        var snapshot = observation.Snapshot; var control = observation.Control; var settings = observation.Settings;
        var target = observation.SoftwareTarget ?? snapshot.Target;
        if (!snapshot.Connected) { Reset(); return TemperatureStabilityState.Fault; }
        if (!control.ControlEnabled) { Reset(); return TemperatureStabilityState.Cooling; }
        if (target.Unit != PhysicalUnit.Kelvin || snapshot.Readback.Unit != PhysicalUnit.Kelvin)
            throw new ArgumentException("Temperature observations require Kelvin.");
        if (target != previousTarget || settings != previousSettings) since = null;
        previousTarget = target; previousSettings = settings;
        var error = Math.Abs(target.Value - snapshot.Readback.Value);
        if (error > settings.SettlingThreshold.Value) { since = null; return TemperatureStabilityState.Ramping; }
        if (error > settings.Tolerance.Value) { since = null; return TemperatureStabilityState.Settling; }
        if (since is null || activeElapsed < since) since = activeElapsed;
        return activeElapsed - since.Value >= settings.StableFor ? TemperatureStabilityState.Stable : TemperatureStabilityState.Reached;
    }
}

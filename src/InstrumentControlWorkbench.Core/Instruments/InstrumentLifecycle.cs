namespace InstrumentControlWorkbench.Core.Instruments;

public enum InstrumentLifecycleState
{
    Disconnected,
    Connecting,
    Idle,
    Configuring,
    Running,
    Stopping,
    Faulted,
    Stale,
}

public enum InstrumentLifecycleEvent
{
    ConnectRequested,
    Connected,
    ConfigureRequested,
    Configured,
    RunRequested,
    StopRequested,
    SafeStateReached,
    FaultDetected,
    HeartbeatExpired,
    Recovered,
    Disconnected,
}

public sealed class InstrumentStateMachine
{
    public InstrumentLifecycleState Apply(
        InstrumentLifecycleState current,
        InstrumentLifecycleEvent evt)
    {
        return (current, evt) switch
        {
            (InstrumentLifecycleState.Disconnected, InstrumentLifecycleEvent.ConnectRequested) => InstrumentLifecycleState.Connecting,
            (InstrumentLifecycleState.Connecting, InstrumentLifecycleEvent.Connected) => InstrumentLifecycleState.Idle,
            (InstrumentLifecycleState.Idle, InstrumentLifecycleEvent.ConfigureRequested) => InstrumentLifecycleState.Configuring,
            (InstrumentLifecycleState.Configuring, InstrumentLifecycleEvent.Configured) => InstrumentLifecycleState.Idle,
            (InstrumentLifecycleState.Idle, InstrumentLifecycleEvent.RunRequested) => InstrumentLifecycleState.Running,
            (InstrumentLifecycleState.Running, InstrumentLifecycleEvent.StopRequested) => InstrumentLifecycleState.Stopping,
            (InstrumentLifecycleState.Stopping, InstrumentLifecycleEvent.SafeStateReached) => InstrumentLifecycleState.Idle,
            (InstrumentLifecycleState.Faulted, InstrumentLifecycleEvent.SafeStateReached) => InstrumentLifecycleState.Idle,
            (InstrumentLifecycleState.Stale, InstrumentLifecycleEvent.Recovered) => InstrumentLifecycleState.Idle,
            (_, InstrumentLifecycleEvent.FaultDetected) => InstrumentLifecycleState.Faulted,
            (_, InstrumentLifecycleEvent.HeartbeatExpired) => InstrumentLifecycleState.Stale,
            (_, InstrumentLifecycleEvent.Disconnected) => InstrumentLifecycleState.Disconnected,
            _ => throw new InvalidOperationException($"Invalid instrument transition: {current} + {evt}"),
        };
    }
}

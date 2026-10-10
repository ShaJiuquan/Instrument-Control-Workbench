using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class InstrumentStateMachineTests
{
    [Fact]
    public void AllowsNormalRunAndSafeStopFlow()
    {
        var machine = new InstrumentStateMachine();

        var state = machine.Apply(InstrumentLifecycleState.Disconnected, InstrumentLifecycleEvent.ConnectRequested);
        state = machine.Apply(state, InstrumentLifecycleEvent.Connected);
        state = machine.Apply(state, InstrumentLifecycleEvent.RunRequested);
        state = machine.Apply(state, InstrumentLifecycleEvent.StopRequested);
        state = machine.Apply(state, InstrumentLifecycleEvent.SafeStateReached);

        Assert.Equal(InstrumentLifecycleState.Idle, state);
    }

    [Fact]
    public void FaultCanReconnectTransportButCannotDirectlyEnableOutput()
    {
        var machine = new InstrumentStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.Apply(InstrumentLifecycleState.Faulted, InstrumentLifecycleEvent.RunRequested));
        Assert.Equal(InstrumentLifecycleState.Connecting,
            machine.Apply(InstrumentLifecycleState.Faulted, InstrumentLifecycleEvent.ConnectRequested));
    }

    [Fact]
    public void RejectsRunBeforeConnect()
    {
        var machine = new InstrumentStateMachine();

        Assert.Throws<InvalidOperationException>(() =>
            machine.Apply(InstrumentLifecycleState.Disconnected, InstrumentLifecycleEvent.RunRequested));
    }
}

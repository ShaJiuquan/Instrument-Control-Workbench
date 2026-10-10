using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

// Acquisition reuses the existing paired V/I contract. Internal routing owns
// source-off/switch/settle/enable/read/off; externally managed mode verifies the
// exact route and acquires without changing relays or the caller's output state.
public sealed record ReadSampleResistanceStep : ExperimentStep
{
    public ReadSampleResistanceStep(ReadResistanceStep acquisition, SampleRoutingBinding binding, TimeSpan settle, bool routingManagedExternally = false) : base(acquisition?.Id ?? throw new ArgumentNullException(nameof(acquisition)))
    {
        ArgumentNullException.ThrowIfNull(binding); ExperimentDurations.Validate(settle);
        Acquisition = acquisition; Binding = binding; Settle = settle; RoutingManagedExternally = routingManagedExternally;
    }
    public ReadResistanceStep Acquisition { get; }
    public SampleRoutingBinding Binding { get; }
    public TimeSpan Settle { get; }
    public bool RoutingManagedExternally { get; }
}

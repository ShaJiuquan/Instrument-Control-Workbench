using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Experiments;

// Sample topology supports integrated frames and separately observed source/meter
// acquisition. Internal routing owns
// source-off/switch/settle/enable/read/off; externally managed mode verifies the
// exact route and acquires without changing relays or the caller's output state.
public sealed record ReadSampleResistanceStep : ExperimentStep
{
    public ReadSampleResistanceStep(ResistanceAcquisitionStep acquisition, SampleRoutingBinding binding, TimeSpan settle, bool routingManagedExternally = false, TimeSpan acquisitionSettle = default) : base(acquisition?.Id ?? throw new ArgumentNullException(nameof(acquisition)))
    {
        ArgumentNullException.ThrowIfNull(binding); ExperimentDurations.Validate(settle); ExperimentDurations.Validate(acquisitionSettle);
        if (acquisition is not (ReadResistanceStep or ReadCurrentResistanceStep)) throw new ArgumentException("Unsupported sample acquisition profile.");
        if (acquisition is ReadCurrentResistanceStep && routingManagedExternally) throw new ArgumentException("Independent samples must own source cleanup.");
        if (acquisition is ReadCurrentResistanceStep { Retry.OwnsSourceOutput: false }) throw new ArgumentException("Sample retries must own source output.");
        Acquisition = acquisition; Binding = binding; Settle = settle; RoutingManagedExternally = routingManagedExternally; AcquisitionSettle = acquisitionSettle;
    }
    public ResistanceAcquisitionStep Acquisition { get; }
    public SampleRoutingBinding Binding { get; }
    public TimeSpan Settle { get; }
    public bool RoutingManagedExternally { get; }
    public TimeSpan AcquisitionSettle { get; }
}

using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// One atomic attempt: isolate, configure, connect, settle, acquire and confirm isolation.
// Frame describes the row contract; it is not a separately executed child step.
public sealed record ReadSr830AcFrameStep : ExperimentStep
{
    public ReadSr830AcFrameStep(StepId id, InstrumentId device, InstrumentId isolator, IEnumerable<MatrixCrossPoint> connections,
        Sr830ExcitationSettings excitation, Sr830InputMode input, ParameterId prefix, TimeSpan settle) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentException.ThrowIfNullOrWhiteSpace(isolator.Value);
        ArgumentNullException.ThrowIfNull(connections); ArgumentNullException.ThrowIfNull(excitation);
        if (device == isolator) throw new ArgumentException("Active SR830 acquisition requires a separate isolation device.", nameof(isolator));
        var points = connections.ToArray();
        if (points.Length is < 1 or > 4096 || points.Any(p => p is null) || points.Distinct().Count() != points.Length)
            throw new ArgumentException("Active SR830 acquisition requires one to 4096 distinct isolation crosspoints.", nameof(connections));
        ExperimentDurations.Validate(settle);
        Frame = new(id, device, input, prefix, includeReference: true);
        Device = device; Isolator = isolator; Connections = Array.AsReadOnly(points); Excitation = excitation; Settle = settle;
    }
    public InstrumentId Device { get; }
    public InstrumentId Isolator { get; }
    public IReadOnlyList<MatrixCrossPoint> Connections { get; }
    public Sr830ExcitationSettings Excitation { get; }
    public Sr830InputMode Input => Frame.Input;
    public ParameterId Prefix => Frame.Prefix;
    public TimeSpan Settle { get; }
    public ReadSr830FrameStep Frame { get; }
}

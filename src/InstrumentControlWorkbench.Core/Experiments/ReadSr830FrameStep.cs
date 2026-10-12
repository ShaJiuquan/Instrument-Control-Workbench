using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Eight explicit columns: four raw SNAP values and the observed input subset.
public sealed record ReadSr830FrameStep : ExperimentStep
{
    public ReadSr830FrameStep(StepId id, InstrumentId device, Sr830InputMode input, ParameterId prefix) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(prefix);
        if (!Enum.IsDefined(input)) throw new ArgumentOutOfRangeException(nameof(input));
        Device = device; Input = input; Prefix = prefix;
        var suffixes = new[] { ".x_v", ".y_v", ".r_v", ".theta_deg", ".input_mode", ".sensitivity_v", ".time_constant_s", ".filter_slope_db" };
        Channels = Array.AsReadOnly(Sr830Parameters.All.Select((p, i) => new ReadChannel(new(prefix.Value + suffixes[i]), new(device, p.Id))).ToArray());
    }
    public InstrumentId Device { get; }
    public Sr830InputMode Input { get; }
    public ParameterId Prefix { get; }
    public IReadOnlyList<ReadChannel> Channels { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureValues(Sr830FrameObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Settings.Input != Input) throw new ArgumentException("Lock-in frame input differs from the frozen acquisition.");
        var frame = observation.Frame; var settings = observation.Settings.CaptureParameters();
        var raw = new[] { frame.X, frame.Y, frame.R, frame.Theta };
        return Channels.Select((c, i) => KeyValuePair.Create(c.Column, i < 4 ? raw[i] : settings[c.Address.Parameter])).ToDictionary();
    }
}

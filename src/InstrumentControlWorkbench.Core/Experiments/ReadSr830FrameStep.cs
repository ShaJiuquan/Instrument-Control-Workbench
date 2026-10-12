using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

// Eight explicit columns: four raw SNAP values and the observed input subset.
public sealed record ReadSr830FrameStep : ExperimentStep
{
    public ReadSr830FrameStep(StepId id, InstrumentId device, Sr830InputMode input, ParameterId prefix, bool includeReference = false) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(prefix);
        if (!Enum.IsDefined(input)) throw new ArgumentOutOfRangeException(nameof(input));
        Device = device; Input = input; Prefix = prefix; IncludeReference = includeReference;
        var suffixes = new[] { ".x_v", ".y_v", ".r_v", ".theta_deg", ".input_mode", ".sensitivity_v", ".time_constant_s", ".filter_slope_db" };
        var channels = Sr830Parameters.All.Select((p, i) => new ReadChannel(new(prefix.Value + suffixes[i]), new(device, p.Id))).ToList();
        if (includeReference)
        {
            var referenceSuffixes = new[] { ".reference_source", ".reference_frequency_hz", ".sine_amplitude_v", ".harmonic" };
            channels.AddRange(Sr830ReferenceParameters.All.Select((p, i) => new ReadChannel(new(prefix.Value + referenceSuffixes[i]), new(device, p.Id))));
        }
        Channels = channels.AsReadOnly();
    }
    public bool IncludeReference { get; }
    public InstrumentId Device { get; }
    public Sr830InputMode Input { get; }
    public ParameterId Prefix { get; }
    public IReadOnlyList<ReadChannel> Channels { get; }
    public IReadOnlyDictionary<ParameterId, Quantity> CaptureValues(Sr830FrameObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Settings.Input != Input) throw new ArgumentException("Lock-in frame input differs from the frozen acquisition.");
        var frame = observation.Frame; var settings = new Dictionary<ParameterId, Quantity>(observation.Settings.CaptureParameters());
        if (IncludeReference)
        {
            var reference = observation.Reference ?? throw new ArgumentException("Reference-enabled frame requires observed reference readback.");
            foreach (var value in reference.CaptureParameters()) settings.Add(value.Key, value.Value);
        }
        var raw = new[] { frame.X, frame.Y, frame.R, frame.Theta };
        return Channels.Select((c, i) => KeyValuePair.Create(c.Column, i < 4 ? raw[i] : settings[c.Address.Parameter])).ToDictionary();
    }
}

using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Core.Experiments;

public sealed record CurrentSourcePreparation
{
    public CurrentSourcePreparation(InstrumentId device, Keithley6221Settings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Value); ArgumentNullException.ThrowIfNull(settings);
        Device = device; Settings = settings;
    }
    public InstrumentId Device { get; }
    public Keithley6221Settings Settings { get; }
}

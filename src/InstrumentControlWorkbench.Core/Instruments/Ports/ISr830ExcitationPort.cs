namespace InstrumentControlWorkbench.Core.Instruments.Ports;

// Explicit active-oscillator capability, separate from passive input acquisition.
// This port cannot turn the sine output off; the application owns external isolation.
public interface ISr830ExcitationPort : ISr830MeterPort
{
    Task ConfigureExcitationAsync(Sr830ExcitationSettings settings, CancellationToken token);
}

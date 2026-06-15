namespace InstrumentControlWorkbench.Core.Instruments.Ports;

public interface ISourcePort : IInstrumentPort
{
    Task ConfigureSourceAsync(SourceSetpoint setpoint, CancellationToken cancellationToken);
    Task EnableOutputAsync(CancellationToken cancellationToken);
    Task DisableOutputAsync(CancellationToken cancellationToken);
}

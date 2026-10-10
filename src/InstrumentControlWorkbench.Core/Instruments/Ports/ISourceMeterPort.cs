namespace InstrumentControlWorkbench.Core.Instruments.Ports;

public interface ISourceMeterPort : ISourcePort, IMeterPort
{
    Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken cancellationToken);
}

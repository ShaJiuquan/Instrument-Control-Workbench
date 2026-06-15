namespace InstrumentControlWorkbench.Core.Instruments.Ports;

public interface IMeterPort : IInstrumentPort
{
    Task ConfigureSenseAsync(SenseConfiguration configuration, CancellationToken cancellationToken);
    Task<MeasurementValue> ReadAsync(CancellationToken cancellationToken);
}

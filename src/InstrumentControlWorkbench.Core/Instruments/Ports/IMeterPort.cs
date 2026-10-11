namespace InstrumentControlWorkbench.Core.Instruments.Ports;

public interface IMeterPort : IInstrumentPort
{
    Task ConfigureSenseAsync(SenseConfiguration configuration, CancellationToken cancellationToken);
    Task<MeasurementValue> ReadAsync(CancellationToken cancellationToken);
    Task RefreshZeroAsync(CancellationToken cancellationToken) => throw new NotSupportedException("This meter does not support explicit zero/reference refresh.");
}

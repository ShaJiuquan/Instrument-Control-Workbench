namespace InstrumentControlWorkbench.Core.Data;

public interface IRunResultListener
{
    Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken cancellationToken);
    Task OnRowAsync(RunId runId, MeasurementDataRow row, CancellationToken cancellationToken);
    Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken cancellationToken);
}

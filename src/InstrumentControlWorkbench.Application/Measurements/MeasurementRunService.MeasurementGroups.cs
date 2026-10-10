using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task ExecuteMeasurementGroupAsync(MeasurementGroupStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        if (run.MeasurementInputs is not null) throw new InvalidOperationException("Measurement point groups cannot nest.");
        var first = run.RowsAcquired + 1; var inputs = new List<MeasurementDataRow>(step.InputCount);
        run.MeasurementInputs = inputs;
        try
        {
            foreach (var member in step.Members) await ExecuteStepAsync(member.Body, run, listeners, ivPlot);
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
        }
        finally { run.MeasurementInputs = null; }
        var row = MeasurementGroupSummary.BuildRow(step, first, inputs);
        await CommitRowAsync(row, step.Id, run, listeners);
    }
}

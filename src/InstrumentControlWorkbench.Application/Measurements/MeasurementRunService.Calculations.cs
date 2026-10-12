using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task ExecuteCalculatedMeasurementAsync(CalculatedMeasurementStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners, bool ivPlot)
    {
        foreach (var column in step.Columns) run.Blackboard.Remove(column.Column);
        var before = run.RowsAcquired;
        if (await ExecuteStepAsync(step.Body, run, listeners, ivPlot) != StepOutcome.Success || run.RowsAcquired != before + step.InputCount || run.LastRow is null) throw new InvalidOperationException("Calculated measurement did not produce its complete fresh acquisition.");
        run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
        var row = CalculationSummary.BuildRow(step, run.RowsAcquired, run.LastRow);
        if (!step.ContinueOnUnavailable && row.Calculation!.Outcomes.Any(outcome => outcome.Issue != CalculationIssue.None)) throw new InvalidOperationException("Calculated results unavailable: " + string.Join(", ", row.Calculation.Outcomes.Where(outcome => outcome.Issue != CalculationIssue.None)));
        run.DataSet!.Append(row); run.RowsAcquired++; run.LastRow = row; run.MeasurementInputs?.Add(row);
        foreach (var column in step.Columns)
            if (row.Values.TryGetValue(column.Column, out var value)) run.SetVariable(column.Column, value, step.Id);
            else run.Blackboard.Remove(column.Column);
        foreach (var listener in listeners) await listener.OnRowAsync(run.Id, row, listener == History ? CancellationToken.None : run.Token);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DataRowReceived, "Calculated measurement results recorded with original input-row evidence.", RunId: run.Id.ToString(), Step: step.Id, Row: row, AcquiredRowCount: run.RowsAcquired), CancellationToken.None);
    }
}

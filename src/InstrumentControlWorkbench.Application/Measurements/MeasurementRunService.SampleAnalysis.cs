using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task ExecuteSampleAnalysisAsync(SampleAnalysisStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> listeners)
    {
        var firstInput = run.RowsAcquired + 1;
        var resistance = new List<Quantity?>();
        MeasurementDataRow? firstRow = null;
        var quality = new List<AcquisitionQuality>();
        var failures = new List<IndependentResistanceFailure>();
        var independent = new List<IndependentResistanceEvidence>();
        foreach (var reading in step.Readings)
        {
            if (await ExecuteStepAsync(reading, run, listeners, ivPlot: false) != StepOutcome.Success)
                throw new InvalidOperationException("A sample acquisition returned an unsupported workflow result.");
            firstRow ??= run.LastRow!;
            quality.AddRange(run.LastRow!.Quality); independent.AddRange(run.LastRow.IndependentResistance); failures.AddRange(run.LastRow.AcquisitionFailures);
            resistance.Add(run.LastRow.Values.TryGetValue(reading.Acquisition.ResistanceColumn, out var value) ? value : null);
        }
        run.Token.ThrowIfCancellationRequested();
        await PauseCheckpointAsync(run);
        var sample = step.ValidateRouting(run.DataSet!.Plan!.Routing!);
        var result = SampleResistanceAnalysis.CalculateAvailable(sample.Topology, sample.Geometry, resistance);
        var values = new Dictionary<ParameterId, Quantity>(run.Axes)
        { [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second) };
        foreach (var column in step.Columns)
            if (result.Values.TryGetValue(column.Kind, out var value)) values.Add(column.Column, value);
        foreach (var observation in firstRow!.Environment)
            if (observation.Value is { } actual) values.Add(observation.Column, actual);
        var row = new MeasurementDataRow(DateTimeOffset.UtcNow, values, analysis: new(step.Id, firstInput, step.Readings.Count, result.Issues), environment: firstRow.Environment, quality: quality, independentResistance: independent, acquisitionFailures: failures, sr830Ac: firstRow.Sr830Ac);
        run.DataSet.Append(row); run.RowsAcquired++;
        run.LastRow = row;
        run.MeasurementInputs?.Add(row);
        foreach (var column in step.Columns)
            if (result.Values.TryGetValue(column.Kind, out var value)) run.SetVariable(column.Column, value, step.Id);
            else run.Blackboard.Remove(column.Column);
        foreach (var listener in listeners)
            await listener.OnRowAsync(run.Id, row, listener == History ? CancellationToken.None : run.Token);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.DataRowReceived,
            $"Analyzed sample group: rows {firstInput}–{firstInput + step.Readings.Count - 1}, issues {result.Issues}; acquisition quality {row.QualityState} ({row.QualityIssues}); observed column evidence {row.ObservedQualityColumns}/{row.Quality.Count}.",
            RunId: run.Id.ToString(), Step: step.Id, Row: row, AcquiredRowCount: run.RowsAcquired), CancellationToken.None);
    }
}

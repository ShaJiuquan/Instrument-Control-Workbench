using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed class InMemoryRunHistory : IRunResultListener
{
    private readonly int maximumRuns;
    private readonly int maximumRowsPerRun;
    public InMemoryRunHistory(int maximumRuns = 32, int maximumRowsPerRun = 1024)
    {
        if (maximumRuns is < 1 or > 1000 || maximumRowsPerRun is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumRuns));
        this.maximumRuns = maximumRuns; this.maximumRowsPerRun = maximumRowsPerRun;
    }
    private readonly object gate = new();
    private readonly Dictionary<RunId, RunDataSetSnapshot> completed = [];
    private readonly Dictionary<RunId, int> observedRows = [];

    public IReadOnlyList<RunDataSetSnapshot> Runs
    {
        get { lock (gate) return completed.Values.OrderByDescending(r => r.StartedAt).ToArray(); }
    }

    public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            if (!observedRows.TryAdd(dataSet.Id, 0)) throw new InvalidOperationException("A result stream cannot start twice.");
        return Task.CompletedTask;
    }

    public Task OnRowAsync(RunId runId, MeasurementDataRow row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!observedRows.TryGetValue(runId, out var count)) throw new InvalidOperationException("The result stream has not started.");
            observedRows[runId] = count + 1;
        }
        return Task.CompletedTask;
    }

    public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dataSet.FinishedAt is null) throw new ArgumentException("History accepts finalized runs only.", nameof(dataSet));
        lock (gate)
        {
            if (!observedRows.TryGetValue(dataSet.Id, out var count) || count != dataSet.TotalRowCount)
                throw new InvalidOperationException("The finalized result count does not match the delivered stream.");
            if (dataSet.RowOffset + dataSet.Rows.Count != dataSet.TotalRowCount) throw new InvalidOperationException("History row-window counts are inconsistent.");
            // A trimmed history preview carries counts, not archival proof.
            if (dataSet.Rows.Count > maximumRowsPerRun)
                dataSet = dataSet with { Rows = Array.AsReadOnly(dataSet.Rows.TakeLast(maximumRowsPerRun).ToArray()), RowWindow = null, CapturedRowOffset = dataSet.TotalRowCount - maximumRowsPerRun, AcquiredRowCount = dataSet.TotalRowCount };
            completed[dataSet.Id] = dataSet;
            while (completed.Count > maximumRuns) completed.Remove(completed.Values.OrderBy(run => run.StartedAt).ThenBy(run => run.Id.Value).First().Id);
            observedRows.Remove(dataSet.Id);
        }
        return Task.CompletedTask;
    }
}

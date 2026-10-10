using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed class InMemoryRunHistory : IRunResultListener
{
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
            if (!observedRows.TryGetValue(dataSet.Id, out var count) || count != dataSet.Rows.Count)
                throw new InvalidOperationException("The finalized result count does not match the delivered stream.");
            completed[dataSet.Id] = dataSet;
            observedRows.Remove(dataSet.Id);
        }
        return Task.CompletedTask;
    }
}

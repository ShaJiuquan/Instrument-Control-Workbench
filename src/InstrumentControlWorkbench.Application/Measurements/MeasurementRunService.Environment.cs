using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private static async Task<IReadOnlyList<EnvironmentObservation>> ReadEnvironmentAsync(IReadOnlyList<EnvironmentRead> reads,
        Dictionary<ParameterId, Quantity> values, RunExecutionContext run)
    {
        var observations = new List<EnvironmentObservation>(reads.Count);
        foreach (var read in reads)
        {
            run.Token.ThrowIfCancellationRequested();
            Quantity? value = null; string? error = null;
            if (read.Address is not { } address) error = "Controller is not bound in the frozen plan.";
            else
            {
                try
                {
                    var actual = await run.Resources!.ReadAsync(address.Device, address.Parameter, run.Token);
                    run.DataSet!.Schema.Columns.Single(c => c.Parameter.Id == read.Column).Parameter.Validate(actual);
                    value = actual;
                }
                catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or ArgumentException or NotSupportedException or OperationCanceledException)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    if (error.Length > 8192) error = error[..8192];
                }
            }
            run.Token.ThrowIfCancellationRequested();
            if (value is { } actualValue) values.Add(read.Column, actualValue);
            observations.Add(new(read.Role, read.Column, read.Address, DateTimeOffset.UtcNow, value, error));
        }
        return observations.AsReadOnly();
    }
}

using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task AcquireCurrentResistanceRowAsync(ReadCurrentResistanceStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        var expected = step.ExpectedProgrammedCurrent is { } request ? Resolve(request, run) : (Quantity?)null;
        var reading = await run.Resources!.ReadCurrentResistanceAsync(step, expected, run.Token); var evidence = reading.Evidence;
        var values = new Dictionary<ParameterId, Quantity>(run.Axes)
        {
            [step.VoltageColumn] = evidence.Voltage,
            [step.CurrentColumn] = evidence.Current,
            [step.ResistanceColumn] = evidence.Resistance,
            [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second),
        };
        run.Variables[step.VoltageColumn] = evidence.Voltage; run.Variables[step.CurrentColumn] = evidence.Current; run.Variables[step.ResistanceColumn] = evidence.Resistance;
        var environment = await ReadEnvironmentAsync(step.EnvironmentReads, values, run);
        await CommitRowAsync(new(DateTimeOffset.UtcNow, values, environment: environment, quality: reading.Quality, independentResistance: [evidence]), step.Id, run, started);
    }
}

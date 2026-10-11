using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task AcquireCurrentResistanceRowAsync(ReadCurrentResistanceStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started, ResolvedSampleRoute? context = null)
    {
        var expected = step.ExpectedProgrammedCurrent is { } request ? Resolve(request, run) : (Quantity?)null;
        var reading = await run.Resources!.ReadCurrentResistanceAsync(step, expected, run.Token, context is null ? null : 1, duration => DwellAsync(run, duration)); var evidence = reading.Evidence;
        if (reading.Failure is { } failure)
        {
            foreach (var column in new[] { step.VoltageColumn, step.CurrentColumn, step.ResistanceColumn }) run.Blackboard.Remove(column);
            var unavailable = new Dictionary<ParameterId, Quantity>(run.Axes) { [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second) };
            var contextReadings = await ReadEnvironmentAsync(step.EnvironmentReads, unavailable, run);
            await CommitRowAsync(new(DateTimeOffset.UtcNow, unavailable, routing: context, environment: contextReadings, acquisitionFailures: [failure]), step.Id, run, started);
            await notifications.PublishAsync(new(Core.Notifications.WorkbenchNotificationKind.LogAppended,
                $"{step.Id}: invalid resistance point ({failure.Status}) after {failure.Attempts} terminal acquisition attempt(s) and confirmed cleanup; continuing.", step.Meter, RunId: run.Id.ToString(), Step: step.Id), CancellationToken.None);
            return;
        }
        evidence = evidence ?? throw new InvalidOperationException("Resistance result has neither successful evidence nor failure proof.");
        if (evidence.Retry is { } retried) await notifications.PublishAsync(new(Core.Notifications.WorkbenchNotificationKind.LogAppended,
            $"{step.Id}: voltage acquisition succeeded on attempt2 after {retried.Kind}; fresh idle confirmation recorded.", step.Meter, RunId: run.Id.ToString(), Step: step.Id), CancellationToken.None);
        var values = new Dictionary<ParameterId, Quantity>(run.Axes)
        {
            [step.VoltageColumn] = evidence.Voltage,
            [step.CurrentColumn] = evidence.Current,
            [step.ResistanceColumn] = evidence.Resistance,
            [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second),
        };
        run.SetVariable(step.VoltageColumn, evidence.Voltage, step.Id); run.SetVariable(step.CurrentColumn, evidence.Current, step.Id); run.SetVariable(step.ResistanceColumn, evidence.Resistance, step.Id);
        var environment = await ReadEnvironmentAsync(step.EnvironmentReads, values, run);
        await CommitRowAsync(new(DateTimeOffset.UtcNow, values, routing: context, environment: environment, quality: reading.Quality, independentResistance: [evidence]), step.Id, run, started);
    }
}

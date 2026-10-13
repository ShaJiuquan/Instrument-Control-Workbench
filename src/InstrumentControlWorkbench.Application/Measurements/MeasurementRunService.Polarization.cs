using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task AcquirePolarizationRowAsync(ReadPolarizationStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        for (var attempt = 1; attempt <= step.MaximumAttempts; attempt++)
        {
            if (attempt > 1) AdvancePlanExecution(run, step.Id);
            run.Token.ThrowIfCancellationRequested();
            await run.Resources!.DisableSourceOutputAsync(step.Device, run.Token);
            if (step.RequiredRoute is { } route) await run.Resources.VerifyMatrixRouteAsync(route.Device, route.Closed, run.Token);
            // Configure the first voltage while OFF; ON cannot expose a stale level.
            await run.Resources.ConfigureSourceMeterAsync(step.Device, step.Setpoint(step.PolarizationVoltage), step.Sense, run.Token);
            await run.Resources.EnableSourceOutputAsync(step.Device, run.Token, step.Sense.Mode);
            if (!await PolarizationDwellAsync(step, run, step.PolarizationDuration)) continue;
            if (step.InterPulseDuration > TimeSpan.Zero)
            {
                await run.Resources.WriteAsync(step.Device, Keithley2450Parameters.SourceVoltage.Id, step.InterPulseVoltage, run.Token);
                if (!await PolarizationDwellAsync(step, run, step.InterPulseDuration)) continue;
            }
            await run.Resources.WriteAsync(step.Device, Keithley2450Parameters.SourceVoltage.Id, step.ReadVoltage, run.Token);
            if (!await PolarizationDwellAsync(step, run, step.ReadDelay)) continue;
            if (step.RequiredRoute is { } before) await run.Resources.VerifyMatrixRouteAsync(before.Device, before.Closed, run.Token);
            var frame = await run.Resources.ReadPolarizationAsync(step, run.Token);
            if (step.RequiredRoute is { } after) await run.Resources.VerifyMatrixRouteAsync(after.Device, after.Closed, run.Token);
            // No sample row or blackboard value exists until independent OFF proof.
            await run.Resources.DisableSourceOutputAsync(step.Device, CancellationToken.None);
            run.Token.ThrowIfCancellationRequested();
            var attempts = new Quantity(attempt, PhysicalUnit.Dimensionless);
            var values = new Dictionary<ParameterId, Quantity>(run.Axes)
            {
                [step.VoltageColumn] = frame.Source,
                [step.CurrentColumn] = frame.Sense,
                [step.AttemptsColumn] = attempts,
                [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second),
            };
            var environment = await ReadEnvironmentAsync(step.EnvironmentReads, values, run);
            run.SetVariable(step.VoltageColumn, frame.Source, step.Id); run.SetVariable(step.CurrentColumn, frame.Sense, step.Id); run.SetVariable(step.AttemptsColumn, attempts, step.Id);
            var quality = new[] { step.VoltageColumn, step.CurrentColumn }.Select(column => new AcquisitionQuality(step.Id, step.Device, column, frame.Timestamp, frame.Status, frame.AcquisitionConfiguration));
            await CommitRowAsync(new(DateTimeOffset.UtcNow, values, environment: environment, quality: quality), step.Id, run, started); return;
        }
        throw new InvalidOperationException("Polarization attempt limit exceeded after interrupted pulses; no result retained.");
    }
    private async Task<bool> PolarizationDwellAsync(ReadPolarizationStep step, RunExecutionContext run, TimeSpan duration)
    {
        run.Token.ThrowIfCancellationRequested();
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var timer = Task.Delay(duration, timerCancellation.Token);
        var winner = await Task.WhenAny(timer, run.PauseRequested);
        if (winner == timer && run.PauseGate is null) { await timer; return true; }
        timerCancellation.Cancel();
        try { await timer; } catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested) { }
        run.Token.ThrowIfCancellationRequested();
        // Capture OFF as the pause baseline, so resume never reapplies the
        // interrupted stage voltage. The outer loop starts a complete attempt.
        await run.Resources!.DisableSourceOutputAsync(step.Device, CancellationToken.None);
        await notifications.PublishAsync(new(WorkbenchNotificationKind.LogAppended, $"{step.Id}: pulse interrupted; output OFF confirmed, next attempt will restart from polarization voltage.", step.Device, RunId: run.Id.ToString(), Step: step.Id), CancellationToken.None);
        await PauseCheckpointAsync(run); return false;
    }
}

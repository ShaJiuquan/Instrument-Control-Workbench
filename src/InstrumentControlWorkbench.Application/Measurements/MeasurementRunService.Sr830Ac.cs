using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task AcquireSr830AcRowAsync(ReadSr830AcFrameStep step, RunExecutionContext run, IReadOnlyList<IRunResultListener> started)
    {
        var firstAttempt = true;
        while (true)
        {
            if (!firstAttempt) AdvancePlanExecution(run, step.Id);
            firstAttempt = false;
            run.Token.ThrowIfCancellationRequested();
            var pauseRequested = run.PauseRequested;
            var beforeConfiguration = await run.Resources!.PrepareSr830AcAsync(step, run.Token);
            if (await RestartPausedAcAttemptAsync(step, run, pauseRequested)) continue;
            await run.Resources.ConnectSr830AcAsync(step, run.Token);
            if (!await Sr830AcDwellAsync(step, run, pauseRequested)) continue;
            var observed = await run.Resources.ReadSr830AcFrameAsync(step, run.Token);
            // No row or blackboard evidence exists until a fresh, independent open confirmation.
            var afterAcquisition = await run.Resources.ConfirmSr830AcOpenAsync(step, CancellationToken.None);
            run.Token.ThrowIfCancellationRequested();
            if (await RestartPausedAcAttemptAsync(step, run, pauseRequested)) continue;
            var evidence = new Sr830AcEvidence(step.Id, step.Device, step.Isolator, step.Connections,
                observed.Frame.Reference!, observed.Frame.Frame.CapturedAt, Sr830AcRouteObservation.From(beforeConfiguration),
                Sr830AcRouteObservation.From(observed.Before), Sr830AcRouteObservation.From(observed.After), Sr830AcRouteObservation.From(afterAcquisition));
            var acquired = step.Frame.CaptureValues(observed.Frame);
            var values = new Dictionary<ParameterId, Quantity>(run.Axes)
            { [ExperimentPlanCompiler.Elapsed.Id] = new(run.Elapsed.Elapsed.TotalSeconds, PhysicalUnit.Second) };
            foreach (var value in acquired) { values.Add(value.Key, value.Value); run.SetVariable(value.Key, value.Value, step.Id); }
            var quality = step.Frame.Channels.Take(4).Select(c => new AcquisitionQuality(step.Id, step.Device, c.Column, observed.Frame.Frame.CapturedAt, null));
            await CommitRowAsync(new(afterAcquisition.Timestamp, values, quality: quality, sr830Ac: [evidence]), step.Id, run, started);
            return;
        }
    }

    private async Task<bool> RestartPausedAcAttemptAsync(ReadSr830AcFrameStep step, RunExecutionContext run, Task pauseRequested)
    {
        if (!pauseRequested.IsCompleted && run.PauseGate is null) return false;
        await run.Resources!.ConfirmSr830AcOpenAsync(step, CancellationToken.None);
        run.Token.ThrowIfCancellationRequested();
        // Pause captures an already isolated route; resume cannot reclose an interrupted attempt.
        await PauseCheckpointAsync(run);
        return true;
    }

    private async Task<bool> Sr830AcDwellAsync(ReadSr830AcFrameStep step, RunExecutionContext run, Task pauseRequested)
    {
        run.Token.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var delay = Task.Delay(step.Settle, cancellation.Token);
        var winner = await Task.WhenAny(delay, pauseRequested);
        if (winner == delay) await delay;
        else
        {
            cancellation.Cancel();
            try { await delay; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        run.Token.ThrowIfCancellationRequested();
        return !await RestartPausedAcAttemptAsync(step, run, pauseRequested);
    }
}

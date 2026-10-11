using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task ExecuteTemperatureRampAsync(SoftwareTemperatureRampStep step, RunExecutionContext run)
    {
        var target = Resolve(step.Target, run);
        TemperatureRampPath.ValidateTemperature(target);
        var definition = devices.GetAxisDefinition(step.Device);
        definition.Target.ValidateWrite(target);
        if (step.Rate is not null) definition.Target.ValidateWrite(new(TemperatureRampPath.MilliKelvin(target) / 1000.0, PhysicalUnit.Kelvin));
        var start = await run.Resources!.BeginSoftwareTemperatureRampAsync(step.Device, target, run.Token);
        if (step.Rate is not { } rate)
        {
            await PauseCheckpointAsync(run);
            run.ExecutedSteps++;
            await run.Resources.WriteSoftwareTemperatureTargetAsync(step.Device, target, run.Token);
            return;
        }
        var path = new TemperatureRampPath(start, target, rate);
        // Endpoint rounding can fall outside a narrower adapter's target range.
        definition.Target.ValidateWrite(path.Targets().FirstOrDefault(path.RoundedTarget));
        definition.Target.ValidateWrite(path.RoundedTarget);
        if (path.UpdateCount == 0)
        {
            await PauseCheckpointAsync(run);
            run.ExecutedSteps++;
            await run.Resources.WriteSoftwareTemperatureTargetAsync(step.Device, path.RoundedTarget, run.Token);
            return;
        }
        long updated = 0;
        foreach (var next in path.Targets())
        {
            await PauseCheckpointAsync(run);
            run.ExecutedSteps++;
            await run.Resources.WriteSoftwareTemperatureTargetAsync(step.Device, next, run.Token);
            // Preserve the remaining active tick across pause; no wall-clock
            // catch-up and no delay after programming the final setpoint.
            if (++updated < path.UpdateCount) await DwellAsync(run, TimeSpan.FromSeconds(TemperatureRampPath.TickSeconds));
        }
    }
    private async Task WaitTemperatureAsync(WaitTemperatureStep wait, RunExecutionContext run)
    {
        var target = Resolve(wait.Target, run);
        var startedAt = run.ActiveElapsed;
        var observer = new TemperatureStabilityObserver();
        TemperatureStabilityState? previous = null;
        var pauseVersion = run.PauseVersion;
        while (true)
        {
            run.Token.ThrowIfCancellationRequested();
            await PauseCheckpointAsync(run);
            if (pauseVersion != run.PauseVersion) { observer.Reset(); previous = null; pauseVersion = run.PauseVersion; }
            if (run.ActiveElapsed - startedAt > wait.Timeout) throw new TimeoutException($"{wait.Id}: temperature stability timeout.");
            var observation = await run.Resources!.ReadTemperatureStabilityAsync(wait.Device, run.Token);
            var now = run.ActiveElapsed;
            // Software ramps round the requested target to integer millikelvin.
            // Larger target drift is not a valid stability observation.
            if (!observation.Snapshot.Connected || Math.Abs(observation.Snapshot.Target.Value - target.Value) > 0.000500001)
                throw new InvalidOperationException($"{wait.Id}: temperature controller disconnected or target changed during wait.");
            var state = observer.Observe(observation, now);
            if (state != previous)
            {
                await notifications.PublishAsync(new(WorkbenchNotificationKind.LogAppended,
                    $"Temperature wait: {state} (tolerance {observation.Settings.Tolerance.Value} K; settling threshold {observation.Settings.SettlingThreshold.Value} K; stable duration {observation.Settings.StableFor.TotalSeconds} s).",
                    wait.Device, RunId: run.Id.ToString(), Step: wait.Id), run.Token);
                previous = state;
            }
            if ((state == TemperatureStabilityState.Stable || wait.Mode == TemperatureWaitMode.Reached && state == TemperatureStabilityState.Reached) && now - startedAt <= wait.Timeout) return;
            var remaining = wait.Timeout - (now - startedAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException($"{wait.Id}: temperature stability timeout.");
            await DwellAsync(run, remaining < wait.PollInterval ? remaining : wait.PollInterval);
        }
    }

}

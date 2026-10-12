using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    private async Task WaitTemperatureStateAsync(WaitTemperatureStateStep step, RunExecutionContext run)
    {
        TemperatureStabilityState? previous = null; var pauseVersion = run.PauseVersion;
        while (true)
        {
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
            if (pauseVersion != run.PauseVersion)
            {
                await run.Resources!.ResetTemperatureEvidenceAsync(step.Device, run.Token);
                previous = null; pauseVersion = run.PauseVersion;
            }
            var observation = await run.Resources!.ReadTemperatureStabilityAsync(step.Device, run.Token);
            run.Token.ThrowIfCancellationRequested();
            if (!observation.Snapshot.Connected) throw new InvalidOperationException($"{step.Id}: temperature controller disconnected during state wait.");
            var state = observation.Runtime?.State ?? throw new InvalidOperationException($"{step.Id}: controller has no temperature state observation.");
            if (state == TemperatureStabilityState.Fault) throw new InvalidOperationException($"{step.Id}: temperature controller state is faulted.");
            if (state != previous)
            {
                await notifications.PublishAsync(new(WorkbenchNotificationKind.LogAppended, $"Temperature state wait: {state}; waiting for {step.Mode}.", step.Device, RunId: run.Id.ToString(), Step: step.Id), run.Token);
                previous = state;
            }
            if (state == TemperatureStabilityState.Stable || step.Mode == TemperatureWaitMode.Reached && state == TemperatureStabilityState.Reached) return;
            await DwellAsync(run, step.PollInterval);
        }
    }
    private async Task WaitAngleAsync(WaitAngleStep step, RunExecutionContext run)
    {
        var requested = step.Target is { } authored ? Resolve(authored, run) : (Quantity?)null;
        if (requested is { } explicitTarget) devices.GetAxisDefinition(step.Device).Target.Validate(explicitTarget);
        while (true)
        {
            run.Token.ThrowIfCancellationRequested(); await PauseCheckpointAsync(run);
            var observed = await run.Resources!.ReadAxisSnapshotAsync(step.Device, run.Token);
            run.Token.ThrowIfCancellationRequested();
            var target = requested ?? observed.Target;
            if (observed.Readback.Unit != PhysicalUnit.Degree || target.Unit != PhysicalUnit.Degree) throw new InvalidOperationException($"{step.Id}: angle wait requires Degree observations.");
            if (Math.Abs(observed.Readback.Value - target.Value) <= step.Tolerance.Value) return;
            await DwellAsync(run, step.PollInterval);
        }
    }
}

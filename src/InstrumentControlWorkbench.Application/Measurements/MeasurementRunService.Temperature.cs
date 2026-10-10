using InstrumentControlWorkbench.Core.Experiments;
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
}

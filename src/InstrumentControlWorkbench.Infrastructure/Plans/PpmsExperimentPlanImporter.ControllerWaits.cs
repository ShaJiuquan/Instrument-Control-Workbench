using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep ControllerWait(string name, JsonObject original, string path, ImportContext c)
    {
        var p = (JsonObject)original.DeepClone();
        Alias(p, "timeout_s", "timeout");
        var angle = name == "wait_angle";
        var field = name == "wait_field";
        if (field) Alias(p, "duration_s", "stable_duration_s");
        if (angle) { Alias(p, "target_deg", "target", "angle"); Alias(p, "tolerance_deg", "tolerance"); }
        else Alias(p, "wait_for", "state");
        Fields(p, angle ? ["target_deg", "tolerance_deg", "timeout_s", "poll_ms"] : field ? ["wait_for", "timeout_s", "poll_ms", "tolerance_t", "duration_s"] : ["wait_for", "timeout_s", "poll_ms"]);
        var timeout = Duration(p, "timeout_s", angle ? 30 : 3600);
        var poll = Number(p, "poll_ms", 500);
        if (poll < 1 || poll != Math.Truncate(poll) || poll > uint.MaxValue - 1) throw new FormatException("poll_ms requires a positive integer in the timer range.");
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, poll)); // PPMS runtime minimum, including raw callers.
        var id = new StepId(timeout == TimeSpan.Zero ? path : path + ".wait");
        ExperimentStep wait;
        if (field)
        {
            var axis = Axis("field", c);
            Choice(p, "wait_for", "holding", "holding");
            wait = new WaitFieldStep(id, axis.Device.Id, new(Number(p, "tolerance_t", 0.0005), PhysicalUnit.Tesla), Duration(p, "duration_s", 0), interval);
        }
        else if (angle)
        {
            var axis = Axis("angle", c);
            var target = p.ContainsKey("target_deg") ? new ConstantValue(new Quantity(Number(p, "target_deg"), PhysicalUnit.Degree)) : null;
            if (target is { } explicitTarget)
            {
                if (Math.Abs(explicitTarget.Value.Value) > 360) throw new FormatException("PPMS angle target must lie in -360..360 Degree.");
                axis.Target.Validate(explicitTarget.Value);
            }
            wait = new WaitAngleStep(id, axis.Device.Id, new(Number(p, "tolerance_deg", 0.1), PhysicalUnit.Degree), interval, target);
        }
        else
        {
            var axis = Axis("temperature", c);
            var mode = Text(p, "wait_for", name == "wait_temp_reached" ? "reached" : "stable").Trim().ToLowerInvariant() switch
            {
                "reached" => TemperatureWaitMode.Reached,
                "stable" => TemperatureWaitMode.Stable,
                _ => throw new NotSupportedException("Temperature state waits support reached or stable."),
            };
            wait = new WaitTemperatureStateStep(id, axis.Device.Id, mode, interval);
        }
        return timeout == TimeSpan.Zero ? wait : new TimeoutStep(new(path), timeout, wait);
    }
}

using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static IReadOnlyList<EnvironmentRead> MeasurementEnvironment(string path, ImportContext c)
        => Enum.GetValues<EnvironmentRole>().Select(role =>
        {
            var name = role.ToString().ToLowerInvariant();
            var suffix = role switch { EnvironmentRole.Temperature => "T_K", EnvironmentRole.Field => "H_T", _ => "angle_deg" };
            var controller = c.Bindings.Axes.ContainsKey(name) ? Axis(name, c) : null;
            return new EnvironmentRead(role, new(path + ".env." + suffix), controller is null ? null : new(controller.Device.Id, controller.Readback.Id));
        }).ToArray();
}

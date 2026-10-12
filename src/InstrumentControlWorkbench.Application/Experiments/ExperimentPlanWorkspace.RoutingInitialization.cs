using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed partial class ExperimentPlanWorkspace
{
    public ValidatedPlanDocument InitializeRouting(ExperimentPlan plan, RoutingConfiguration routing)
    {
        ArgumentNullException.ThrowIfNull(routing); runs.PreviewPlan(plan);
        if (plan.Routing is not null) throw new InvalidOperationException("The plan already has a routing catalog; initialization cannot replace it.");
        var matrix = runs.MatrixDefinitions.Single(definition => definition.Device.Id == routing.Matrix);
        foreach (var wiring in routing.Wirings)
            matrix.Validate(new[] { wiring.SourcePlus, wiring.SourceMinus, wiring.SensePlus, wiring.SenseMinus }.Select(port => new MatrixCrossPoint(port.Slot, port.Row, 1)));
        foreach (var connection in routing.Channels.SelectMany(channel => channel.Connections).Where(connection => connection.Enabled))
        {
            if (connection.Slot is not { } slot || connection.Column is not { } column) throw new ArgumentException("Initial routing connections require physical card and column addresses.");
            matrix.Validate([new(slot, 1, column)]);
        }
        foreach (var sample in routing.Samples)
            foreach (var wiring in routing.Wirings) runs.ValidateSampleRoutes(routing, sample.Id, wiring.Id);
        return Inspect(new(plan.Name, plan.Root, plan.Preparations, plan.Metadata, routing, plan.CurrentSourcePreparations, plan.MaximumStepExecutions));
    }
}

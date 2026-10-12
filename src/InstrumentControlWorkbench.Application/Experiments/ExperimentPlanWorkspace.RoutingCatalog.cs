using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed partial class ExperimentPlanWorkspace
{
    public ValidatedPlanDocument UpdateRoutingCatalog(ExperimentPlan plan, IReadOnlyList<ChannelProfile> channels, IReadOnlyList<InstrumentWiringProfile> wirings, bool synchronizeIndependentRoutes = false)
    {
        var structure = documents as IExperimentPlanStructureDocument ?? throw new NotSupportedException("Catalog editing requires a document adapter that exposes typed plan structure.");
        runs.PreviewPlan(plan); var original = plan.Routing ?? throw new InvalidOperationException("Create or import a routing catalog first.");
        var updated = new RoutingConfiguration(original.Matrix, channels, wirings, original.Samples);
        var matrix = runs.MatrixDefinitions.Single(definition => definition.Device.Id == original.Matrix);
        // Only changed profiles need new physical admission; unchanged imported
        // presentation-only/unbound records retain their exact original meaning.
        foreach (var channel in updated.Channels.Where(channel => !original.Channels.Any(old => ReferenceEquals(old, channel))))
        {
            var prior = original.Channels.SingleOrDefault(old => StringComparer.OrdinalIgnoreCase.Equals(old.Id.Value, channel.Id.Value));
            foreach (var connection in channel.Connections.Where(connection => connection.Enabled))
            {
                var old = prior?.Connections.SingleOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.Key.Value, connection.Key.Value));
                if (old is not null && old.Slot == connection.Slot && old.Column == connection.Column && old.Enabled == connection.Enabled) continue;
                if (connection.Slot is not { } slot || connection.Column is not { } column) throw new ArgumentException("Edited executable channels require physical card and column addresses.");
                matrix.Validate([new(slot, 1, column)]);
            }
        }
        foreach (var wiring in updated.Wirings.Where(wiring => !original.Wirings.Any(old => ReferenceEquals(old, wiring))))
        {
            var source = runs.DeviceDescriptors.Single(device => device.Id == wiring.SourceMeter);
            if (source.StopPolicy != DeviceStopPolicy.OutputOff || source.Model is not ("K2450" or "K6221") || !matrix.GuardedSources.Contains(source.Id)) throw new ArgumentException("Edited wiring requires a registered supported matrix-guarded source.");
            foreach (var guard in matrix.GuardedSources)
                if (runs.DeviceDescriptors.Single(device => device.Id == guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Matrix guards require registered output-off sources.");
            if (wiring.VoltageMeter is { } meter)
            {
                var descriptor = runs.DeviceDescriptors.Single(device => device.Id == meter);
                if (descriptor.Model != "K2182" || descriptor.StopPolicy != DeviceStopPolicy.AbortAcquisition) throw new ArgumentException("Independent wiring requires a registered K2182 meter.");
            }
            else if (source.Model == "K6221") throw new ArgumentException("K6221 wiring requires an independent meter.");
            matrix.Validate(new[] { wiring.SourcePlus, wiring.SourceMinus, wiring.SensePlus, wiring.SenseMinus }.Select(port => new MatrixCrossPoint(port.Slot, port.Row, 1)));
        }
        // Even dormant sample references may not be orphaned by removal/rename.
        foreach (var electrode in updated.Samples.SelectMany(sample => sample.Electrodes))
        {
            var channel = updated.Channels.SingleOrDefault(channel => StringComparer.OrdinalIgnoreCase.Equals(channel.Id.Value, electrode.Channel.Value)) ?? throw new ArgumentException("A sample still references the removed channel.");
            if (!channel.Connections.Any(connection => StringComparer.OrdinalIgnoreCase.Equals(connection.Key.Value, electrode.Connection.Value))) throw new ArgumentException("A sample still references the removed connection.");
        }
        var candidate = new ExperimentPlan(plan.Name, plan.Root, plan.Preparations, plan.Metadata, updated, plan.CurrentSourcePreparations, plan.MaximumStepExecutions);
        if (synchronizeIndependentRoutes) candidate = CoordinateIndependentSampleGuards(plan, candidate, null);
        var ids = structure.DescribeStructure(candidate).Where(node => node.Kind is "read_sample_resistance" or "read_independent_sample_resistance").Select(node => node.Id).ToArray();
        foreach (var sample in structure.ReadSteps(candidate, ids).OfType<ReadSampleResistanceStep>()) runs.ValidateSampleRoutes(updated, sample.Binding.Sample, sample.Binding.Wiring);
        return Inspect(candidate);
    }
}

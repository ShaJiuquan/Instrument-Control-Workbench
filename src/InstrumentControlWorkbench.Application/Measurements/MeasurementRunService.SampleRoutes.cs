using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Measurements;

public sealed partial class MeasurementRunService
{
    // Pure catalog admission against this runtime's immutable device definitions.
    public IReadOnlyList<ResolvedSampleRoute> ValidateSampleRoutes(RoutingConfiguration routing, SampleId sampleId, WiringProfileId wiringId)
    {
        ArgumentNullException.ThrowIfNull(routing); ArgumentNullException.ThrowIfNull(sampleId); ArgumentNullException.ThrowIfNull(wiringId);
        var sample = routing.Samples.Single(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, sampleId.Value));
        var wiring = routing.Wirings.Single(w => StringComparer.OrdinalIgnoreCase.Equals(w.Id.Value, wiringId.Value));
        var matrix = devices.GetMatrixDefinition(routing.Matrix);
        var source = devices.Devices.Single(d => d.Id == wiring.SourceMeter);
        if (source.StopPolicy != DeviceStopPolicy.OutputOff || !matrix.GuardedSources.Contains(source.Id)) throw new ArgumentException("Sample wiring needs a registered matrix-guarded output source.");
        foreach (var guard in matrix.GuardedSources)
            if (devices.Devices.Single(d => d.Id == guard).StopPolicy != DeviceStopPolicy.OutputOff) throw new ArgumentException("Matrix excitation guards must use output-off policy.");
        if (wiring.VoltageMeter is { } meter && devices.Devices.Single(d => d.Id == meter).StopPolicy != DeviceStopPolicy.AbortAcquisition) throw new ArgumentException("Independent wiring needs a registered acquisition meter.");
        var selections = sample.Topology switch
        {
            SampleTopology.TwoWire => new[] { SampleRouteSelection.TwoWireResistance },
            SampleTopology.FourWire => new[] { SampleRouteSelection.FourWireResistance },
            _ => SampleResistanceAnalysis.Selections(sample.Topology).ToArray(),
        };
        var routes = selections.Select(selection => routing.Resolve(new(sample.Id, wiring.Id, selection))).ToArray();
        foreach (var route in routes) matrix.Validate(route.Closed);
        return Array.AsReadOnly(routes);
    }
}

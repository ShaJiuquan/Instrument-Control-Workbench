using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Core.Measurements;

public sealed record SampleResistanceRecipe
{
    public SampleResistanceRecipe(string name, RoutingConfiguration routing, SampleId sample, WiringProfileId wiring,
        SourcePreparation source, TimeSpan settle, Quantity minimumCurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(routing); ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(wiring); ArgumentNullException.ThrowIfNull(source); ExperimentDurations.Validate(settle);
        if (minimumCurrent.Unit != PhysicalUnit.Ampere || minimumCurrent.Value < 0) throw new ArgumentException("Minimum absolute current must be nonnegative Ampere.", nameof(minimumCurrent));
        var profile = routing.Samples.SingleOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Id.Value, sample.Value)) ?? throw new KeyNotFoundException($"Unknown sample {sample}.");
        var selections = profile.Topology switch
        {
            SampleTopology.TwoWire => new[] { SampleRouteSelection.TwoWireResistance },
            SampleTopology.FourWire => new[] { SampleRouteSelection.FourWireResistance },
            SampleTopology.HallBar => new[] { SampleRouteSelection.HallLongitudinal, SampleRouteSelection.HallTransverse },
            SampleTopology.VanDerPauw => new[] { SampleRouteSelection.VdpAbCd, SampleRouteSelection.VdpBaCd, SampleRouteSelection.VdpAbDc, SampleRouteSelection.VdpBaDc,
                SampleRouteSelection.VdpBcDa, SampleRouteSelection.VdpCbDa, SampleRouteSelection.VdpBcAd, SampleRouteSelection.VdpCbAd },
            _ => throw new ArgumentOutOfRangeException(nameof(sample)),
        };
        foreach (var selection in selections)
        {
            var context = routing.Resolve(new(sample, wiring, selection));
            if (context.SourceMeter != source.Device || context.SensingMode != source.Sense.Mode)
                throw new ArgumentException("Source preparation must match the sample wiring and sensing mode.", nameof(source));
        }
        Name = name; Routing = routing; Sample = profile.Id; Wiring = wiring; Source = source; Settle = settle; MinimumCurrent = minimumCurrent;
        Selections = Array.AsReadOnly(selections);
    }
    public string Name { get; }
    public RoutingConfiguration Routing { get; }
    public SampleId Sample { get; }
    public WiringProfileId Wiring { get; }
    public SourcePreparation Source { get; }
    public TimeSpan Settle { get; }
    public Quantity MinimumCurrent { get; }
    public IReadOnlyList<SampleRouteSelection> Selections { get; }
}

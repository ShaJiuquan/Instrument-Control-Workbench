using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Application.Experiments;

public sealed record ConfiguredImportDevice(DeviceDescriptor Descriptor, ConfiguredDeviceCategory Category);
public sealed record ImportWiringSelection(WiringProfileId Wiring, InstrumentId Source, InstrumentId? VoltageMeter = null);

public sealed class ImportRoutingSelection
{
    public ImportRoutingSelection(RoutingProfileCatalog profiles, InstrumentId matrix, IEnumerable<ImportWiringSelection> wirings,
        WiringProfileId channelWiring, TimeSpan routeSettle)
    {
        ArgumentNullException.ThrowIfNull(profiles); ArgumentNullException.ThrowIfNull(wirings); ArgumentNullException.ThrowIfNull(channelWiring);
        ArgumentException.ThrowIfNullOrWhiteSpace(matrix.Value);
        var items = wirings.ToArray();
        if (items.Length is < 1 or > 64 || items.Any(w => w is null || w.Wiring is null || string.IsNullOrWhiteSpace(w.Source.Value)) ||
            items.Select(w => w.Wiring.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length || !items.Any(w => w.Wiring == channelWiring))
            throw new ArgumentException("Choose distinct wiring bindings including the channel wiring.");
        if (routeSettle < TimeSpan.Zero || routeSettle > TimeSpan.FromMilliseconds(uint.MaxValue - 1)) throw new ArgumentOutOfRangeException(nameof(routeSettle));
        Profiles = profiles; Matrix = matrix; Wirings = Array.AsReadOnly(items); ChannelWiring = channelWiring; RouteSettle = routeSettle;
    }
    public RoutingProfileCatalog Profiles { get; }
    public InstrumentId Matrix { get; }
    public IReadOnlyList<ImportWiringSelection> Wirings { get; }
    public WiringProfileId ChannelWiring { get; }
    public TimeSpan RouteSettle { get; }
}

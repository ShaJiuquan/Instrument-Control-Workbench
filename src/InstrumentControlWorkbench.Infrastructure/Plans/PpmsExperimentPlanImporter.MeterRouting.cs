using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    // Source scans already own output-off/switch/enable. Their meter children
    // verify that route; they never switch an energized source between points.
    private static (RouteMatrixStep? Setup, MatrixRouteExpectation? RequiredRoute, InstrumentId? ExcitationSource) MeterRouting(
        JsonObject p, string path, InstrumentId meter, ImportContext c, bool independent)
    {
        var scope = independent ? c.SourceScans.FirstOrDefault() : c.SourceScans.FirstOrDefault(s => s.Device == meter);
        var managed = !string.IsNullOrWhiteSpace(scope.Device.Value);
        var requested = Text(p, "channel", "").Trim();
        var channel = requested.Length == 0 && managed ? scope.Channel : requested;
        var external = channel.Length == 0 || channel.Equals("none", StringComparison.OrdinalIgnoreCase);
        var parentExternal = !managed || scope.Channel.Length == 0 || scope.Channel.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (managed && external != parentExternal)
            throw new NotSupportedException("A meter cannot change or bypass its enclosing source scan's route.");
        if (external) return (null, null, null);

        ImportChannelRoute Resolve(string query)
        {
            ImportChannelRoute binding;
            if (managed && scope.Gate)
            {
                if (independent) throw new NotSupportedException("Gate scans require the explicitly bound integrated meter.");
                var profiles = c.Bindings.ChannelProfiles ?? throw new NotSupportedException("Gate scans require frozen gate profiles.");
                binding = profiles.ResolveGate(query); c.UsesChannelProfiles = true;
            }
            else if (c.Bindings.Channels.TryGetValue(query, out var explicitBinding)) binding = explicitBinding;
            else if (c.Bindings.ChannelProfiles is { } profiles)
            {
                var source = c.Bindings.Preparations.SingleOrDefault(s => s.Device == profiles.Wiring.SourceMeter);
                var mode = independent || source?.Function == SourceFunction.Current ? "curr_dc" : "volt_dc";
                binding = profiles.Resolve(query, independent ? SensingMode.TwoWire : source?.Sense.Mode ?? SensingMode.TwoWire, mode);
                c.UsesChannelProfiles = true;
            }
            else throw new NotSupportedException($"Channel '{query}' needs an explicit meter wiring binding.");

            if (managed && binding.Source != scope.Device || !independent && binding.Source != meter ||
                binding.VoltageMeter != (independent ? meter : null))
                throw new NotSupportedException("Channel source/meter roles differ from the requested acquisition.");
            if (independent && !c.Bindings.CurrentSourcePreparations.Any(s => s.Device == binding.Source) &&
                !c.Bindings.Preparations.Any(s => s.Device == binding.Source && s.Function == SourceFunction.Current))
                throw new NotSupportedException("Independent voltage routing requires an explicitly bound current source.");
            return binding;
        }

        var resolved = Resolve(channel);
        var route = resolved.Route;
        if (managed)
        {
            var inherited = Resolve(scope.Channel).Route;
            if (route.Device != inherited.Device || !route.Closed.ToHashSet().SetEquals(inherited.Closed))
                throw new NotSupportedException("A meter cannot select a different channel inside an energized source scan.");
        }
        var setup = managed ? null : new RouteMatrixStep(new(path + ".route"), route.Device, route.Closed, route.Settle);
        return (setup, new(route.Device, route.Closed), resolved.Source);
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static ExperimentStep ElectrodeTest(JsonObject p, string path, ImportContext c)
    {
        Fields(p, "meas_type", "name", "voltage_v", "compliance_a", "max_connected_ohm", "nplc", "settle_s");
        var original = Source(new JsonObject(), c, SourceFunction.Voltage);
        var profiles = c.Bindings.ChannelProfiles ?? throw new NotSupportedException("Electrode test needs frozen channel profiles and K2450 Force wiring.");
        if (profiles.Wiring.SourceMeter != original.Device || profiles.VoltageMeter is not null || original.Sense.Mode != SensingMode.TwoWire || c.SourceScans.Count != 0)
            throw new NotSupportedException("Electrode test requires bound K2450 two-wire Force ports outside source scans.");
        c.UsesChannelProfiles = true;
        var source = new SourcePreparation(original.Device, SourceFunction.Voltage, new(Number(p, "voltage_v", .1), PhysicalUnit.Volt), new(Number(p, "compliance_a", .0001), PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, Number(p, "nplc", 1), true));
        var maximum = new Quantity(Number(p, "max_connected_ohm", 1e6), PhysicalUnit.Ohm); var settle = Duration(p, "settle_s", .1);
        var counts = new[] { "pair_count", "tested_count", "connected_count", "open_count", "not_configured_count" }.ToDictionary(key => key, key => new ParameterId(path + "." + key));
        var steps = counts.Select(entry => (ExperimentStep)new AssignValueStep(new(path + ".reset." + entry.Key), entry.Value, new ConstantValue(new(0, PhysicalUnit.Dimensionless)))).ToList();
        foreach (var channel in profiles.Catalog.Channels.OrderBy(channel => channel.GroupIndex).ThenBy(channel => channel.Id.Value, StringComparer.Ordinal))
            foreach (var pair in Enum.GetValues<ElectrodePair>())
            {
                var key = path + "." + channel.Id.Value + "." + pair.ToString().ToLowerInvariant();
                var route = profiles.ResolveContinuity(channel.Id, pair);
                var read = new ReadElectrodeContinuityStep(new(key), source, channel.Id, pair, route, settle, maximum, new(key + ".status"), new(key + ".reason"), new(key + ".voltage"), new(key + ".current"), new(key + ".resistance"));
                steps.Add(new SourceOutputStep(new(key + ".off"), source.Device, false));
                if (route is not null) steps.Add(new RouteMatrixStep(new(key + ".route"), route.Device, route.Closed, profiles.Settle));
                steps.Add(read);
                steps.Add(new MutateValueStep(new(key + ".count"), counts["pair_count"], VariableMutation.Add, new(1, PhysicalUnit.Dimensionless)));
                steps.Add(new IfElseStep(new(key + ".tested"), new(read.StatusColumn, ComparisonOperator.NotEqual, new ConstantValue(new(2, PhysicalUnit.Dimensionless))), new MutateValueStep(new(key + ".tested.add"), counts["tested_count"], VariableMutation.Add, new(1, PhysicalUnit.Dimensionless))));
                foreach (var status in Enum.GetValues<ElectrodeContinuityStatus>())
                {
                    var count = status switch { ElectrodeContinuityStatus.Connected => "connected_count", ElectrodeContinuityStatus.Open => "open_count", _ => "not_configured_count" };
                    steps.Add(new IfElseStep(new(key + "." + count), new(read.StatusColumn, ComparisonOperator.Equal, new ConstantValue(new((int)status, PhysicalUnit.Dimensionless))), new MutateValueStep(new(key + "." + count + ".add"), counts[count], VariableMutation.Add, new(1, PhysicalUnit.Dimensionless))));
                }
            }
        return new SequenceStep(new(path), steps);
    }
}

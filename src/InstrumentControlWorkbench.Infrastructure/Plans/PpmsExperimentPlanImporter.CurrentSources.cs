using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    // Format boundary shared by current command, scan and resistance import.
    private sealed record ImportedCurrentSource(InstrumentId Device, CurrentExcitationConfiguration Configuration, SourcePreparation? Smu)
    {
        public bool Pure => Configuration.Model == CurrentSourceModel.Keithley6221;
        public ParameterDescriptor Current => Pure ? Keithley6221Parameters.Current : Keithley2450Parameters.SourceCurrent;
        public ParameterDescriptor Compliance => Pure ? Keithley6221Parameters.Compliance : Keithley2450Parameters.VoltageCompliance;
    }
    private static ImportedCurrentSource CurrentSource(JsonObject p, ImportContext c, string defaultName = "k6221")
    {
        var name = Text(p, "instrument", defaultName).Trim();
        if (!c.Bindings.Instruments.TryGetValue(name, out var device)) throw new NotSupportedException($"Instrument '{name}' needs an explicit current-source binding.");
        var pure = c.Bindings.CurrentSourcePreparations.SingleOrDefault(s => s.Device == device);
        if (pure is not null)
        {
            c.CurrentSources.Add(device); var s = pure.Settings;
            return new(device, new(CurrentSourceModel.Keithley6221, s.Current, s.Compliance, s.ManualRange), null);
        }
        var smu = Source(p, c, SourceFunction.Current, defaultName); return new(device, new(smu.ToSetpoint()), smu);
    }
    private static Quantity? SourceRange(JsonObject p, ImportedCurrentSource source)
    {
        var selector = RangeSelector(p, "source_range_a", PhysicalUnit.Ampere);
        return selector is { } value ? source.Pure ? Keithley6221Settings.ResolveRange(value) : Keithley2450SourceRanges.Resolve(SourceFunction.Current, value) : null;
    }
    private static SetParameterStep SourceRangeStep(string path, ImportedCurrentSource source, Quantity? range)
    {
        var parameter = source.Pure ? range is null ? Keithley6221Parameters.EnableAutoRange : Keithley6221Parameters.ManualRange
            : range is null ? Keithley2450Parameters.EnableSourceAutoRange : Keithley2450Parameters.ManualCurrentRange;
        return Write(path + ".source_range", source.Device, parameter, range?.Value ?? 1);
    }
    private static ExperimentStep CurrentSourceCommand(string name, JsonObject p, string path, ImportContext c)
    {
        if (name == "k6221.set_output")
        {
            Alias(p, "enabled", "output_enabled"); Fields(p, "enabled"); if (!p.ContainsKey("enabled")) throw new FormatException("enabled is required.");
            if (!c.Bindings.Instruments.TryGetValue("k6221", out var id) || !c.Bindings.CurrentSourcePreparations.Any(s => s.Device == id)) throw new NotSupportedException("K6221 commands require the explicit pure current-source binding.");
            var on = Boolean(p, "enabled", false); if (on) c.CurrentSources.Add(id);
            return new SourceOutputStep(new(path), id, on);
        }
        var source = CurrentSource(new(), c); if (!source.Pure) throw new NotSupportedException("K6221 commands cannot impersonate a K2450.");
        switch (name)
        {
            case "k6221.set_current": Alias(p, "current_a", "current"); Fields(p, "current_a"); return Write(path, source.Device, source.Current, Number(p, "current_a"));
            case "k6221.set_compliance": Alias(p, "voltage_v", "compliance_v", "compliance"); Fields(p, "voltage_v"); return Write(path, source.Device, source.Compliance, Number(p, "voltage_v"));
            case "k6221.set_range":
            case "k6221.set_source_range":
                Alias(p, "range_a", "range"); Fields(p, "range_a"); var range = Keithley6221Settings.ResolveRange(new(Math.Abs(Number(p, "range_a")), PhysicalUnit.Ampere));
                return Write(path, source.Device, Keithley6221Parameters.ManualRange, range.Value);
            case "k6221.set_auto_range":
            case "k6221.set_source_auto_range":
                Alias(p, "enabled", "auto_range"); Fields(p, "enabled"); if (!p.ContainsKey("enabled") || !Boolean(p, "enabled", false)) throw new NotSupportedException("Disabling source autorange requires an explicit manual range.");
                return Write(path, source.Device, Keithley6221Parameters.EnableAutoRange, 1);
            default: throw new NotSupportedException("K6221 action has no migrated implementation.");
        }
    }
}

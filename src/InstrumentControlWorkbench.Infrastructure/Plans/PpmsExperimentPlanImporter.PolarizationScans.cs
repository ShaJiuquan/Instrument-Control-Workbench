using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter
{
    private static bool IsPolarizationMode(string mode) => mode.Trim().ToLowerInvariant() is "polarization" or "polarization_voltage" or "pol_voltage" or "scan_pol_voltage";
    private static ExperimentStep PolarizationScan(JsonObject p, JsonObject node, string path, ImportContext c, int depth)
    {
        Fields(p, "axis", "points", "points_mode", "start", "end", "step", "values", "segments", "channel", "voltage_mode");
        if (p["axis"] is not null && Canonical(Text(p, "axis")) != "measure_polarization") throw new FormatException("Polarization axis conflicts with scan name.");
        if (p["voltage_mode"] is not null && !IsPolarizationMode(Text(p, "voltage_mode"))) throw new FormatException("Polarization scan requires a polarization voltage mode.");
        var controls = (JsonObject)p.DeepClone(); controls.Remove("axis"); controls.Remove("channel"); controls.Remove("voltage_mode");
        JsonNode spec;
        if (controls["points"] is JsonObject or JsonArray)
        {
            if (controls.Count != 1) throw new FormatException("Nested points cannot be combined with flat polarization point controls.");
            spec = controls["points"]!;
        }
        else
        {
            var mode = Text(controls, "points_mode", controls.ContainsKey("segments") ? "segments" : controls.ContainsKey("values") ? "list" : controls.ContainsKey("step") ? "step" : "linear").Trim().ToLowerInvariant();
            controls.Remove("points_mode");
            if (mode == "segments") { Fields(controls, "segments"); spec = controls; }
            else { if (controls.ContainsKey("segments")) throw new FormatException("Segments require segment point mode."); controls["mode"] = mode; spec = controls; }
        }
        if (spec is JsonObject list && Text(list, "mode", "linear").Trim().ToLowerInvariant() is "list" or "explicit")
        {
            Fields(list, "mode", "values"); spec = list["values"] as JsonArray ?? throw new FormatException("Explicit polarization points require values array.");
        }
        var values = Points(spec, PhysicalUnit.Volt, c, 0);
        // No source configuration/write/output occurs in this iterator. Each
        // direct measurement child gets context, matching Rust scan_children.
        if (c.NumericVariables.TryGetValue("pol_voltage", out var existing) && existing != PhysicalUnit.Volt) throw new FormatException("Polarization axis alias conflicts with an existing variable unit.");
        c.NumericVariables["pol_voltage"] = PhysicalUnit.Volt;
        var column = new ParameterId(path + ".polarization_voltage");
        var children = node["children"] as JsonArray ?? throw new FormatException("Polarization scan requires children.");
        var channel = p["channel"] is null ? null : Text(p, "channel");
        for (var index = 0; index < children.Count; index++)
            if (children[index] is JsonObject child && Text(child, "type", "measurement").Equals("measurement", StringComparison.OrdinalIgnoreCase))
            {
                c.PolarizationChildren.Add(path + "." + index, (column, channel));
                c.IteratorChildren[path + "." + index] = column;
            }
        return new ForEachStep(new(path + ".scan"), column, values,
            new SequenceStep(new(path + ".body"), [new AssignValueStep(new(path + ".publish.pol_voltage"), new("ppms.axis.pol_voltage"), new VariableValue(column)), .. Children(node, path, c, depth)]));
    }
}

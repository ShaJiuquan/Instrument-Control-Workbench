using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class PpmsExperimentPlanImporter : IExperimentPlanImporter
{
    public ExperimentPlan Parse(string text, ExperimentImportBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(bindings);
        if (Encoding.UTF8.GetByteCount(text) > JsonExperimentPlanDocument.MaximumBytes) throw new FormatException("PPMS document exceeds 4 MiB.");
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 512 }) as JsonObject ?? throw new FormatException("PPMS root must be an object.");
        ValidateTree(root);
        var context = new ImportContext(bindings);
        var step = Decode(root, "ppms.root", context, 0);
        var plan = new ExperimentPlan(Text(root, "experiment_id", Text(root, "label", "Imported PPMS experiment")), step,
            bindings.Preparations.Where(p => context.Sources.Contains(p.Device)), context.Metadata, context.UsesSamples ? bindings.Samples!.Routing : context.UsesChannelProfiles ? bindings.ChannelProfiles!.Routing : null,
            bindings.CurrentSourcePreparations.Where(p => context.CurrentSources.Contains(p.Device)));
        // Also bound generated wrapper nodes and the native document before returning.
        _ = new JsonExperimentPlanDocument().Serialize(plan);
        return plan;
    }
    public async Task<ExperimentPlan> LoadAsync(string path, ExperimentImportBindings bindings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int length;
        if (stream.Length > JsonExperimentPlanDocument.MaximumBytes) throw new IOException("PPMS document exceeds 4 MiB.");
        while ((length = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + length > JsonExperimentPlanDocument.MaximumBytes) throw new IOException("PPMS document exceeds 4 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, length), token);
        }
        token.ThrowIfCancellationRequested(); var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        return Parse(text.StartsWith('\uFEFF') ? text[1..] : text, bindings);
    }
    private static void ValidateTree(JsonObject root)
    {
        var pending = new Stack<(JsonObject Node, string Path, int Depth, int GeneratedDepth)>();
        pending.Push((root, "ppms.root", 0, 0));
        var nodes = 0;
        while (pending.TryPop(out var entry))
        {
            if (entry.Depth > 128 || ++nodes > 10_000)
                throw new FormatException($"PPMS node {entry.Path}: tree exceeds depth/node limits.");
            try
            {
                var wrappers = (entry.Node.ContainsKey("_onSuccess") || entry.Node.ContainsKey("_onFailure") ? 1 : 0)
                    + (entry.Node.ContainsKey("_skipIf") ? 1 : 0) + (entry.Node.ContainsKey("_failureIf") ? 1 : 0);
                if (entry.GeneratedDepth + wrappers > 128)
                    throw new FormatException($"PPMS node {entry.Path}: generated tree exceeds depth limits.");
                if (entry.Node["children"] is not JsonArray children) continue;
                for (var index = children.Count - 1; index >= 0; index--)
                    pending.Push((Object(children[index]), entry.Path + "." + index, entry.Depth + 1, entry.GeneratedDepth + wrappers + 1));
            }
            catch (ArgumentException error)
            { throw new FormatException($"PPMS node {entry.Path}: {error.Message}", error); }
        }
    }
    private sealed class ImportContext(ExperimentImportBindings bindings)
    {
        public ExperimentImportBindings Bindings { get; } = bindings;
        public HashSet<InstrumentId> Sources { get; } = [];
        public HashSet<InstrumentId> CurrentSources { get; } = [];
        public Dictionary<string, Quantity> Metadata { get; } = [];
        public bool UsesSamples { get; set; }
        public bool UsesChannelProfiles { get; set; }
        public Stack<(InstrumentId Device, string Channel, Core.Parameters.ParameterId Axis, bool Gate)> SourceScans { get; } = new();
        public Dictionary<string, (ParameterId Variable, string? Channel)> PolarizationChildren { get; } = new();
        public Dictionary<string, ParameterId> IteratorChildren { get; } = new();
        public Dictionary<string, PhysicalUnit> NumericVariables { get; } = new();
        public int Nodes { get; set; }
        public int Values { get; set; }
    }
    private static ExperimentStep Decode(JsonObject node, string path, ImportContext c, int depth)
    {
        bool hasControls;
        try { hasControls = node.Any(p => p.Key is "_onSuccess" or "_onFailure" or "_skipIf" or "_failureIf"); }
        catch (ArgumentException error) { throw new FormatException($"PPMS node {path}: {error.Message}", error); }
        return hasControls ? DecodeControls(node, path, c, depth) : DecodeCore(node, path, c, depth);
    }
    private static ExperimentStep DecodeControls(JsonObject node, string path, ImportContext c, int depth)
    {
        string? diagnosticLine = null;
        try
        {
            diagnosticLine = node["source_line"]?.ToString();
            var success = node.ContainsKey("_onSuccess") ? DecodeMutation(Object(node["_onSuccess"]), path + ".onSuccess", c) : null;
            var failure = node.ContainsKey("_onFailure") ? DecodeMutation(Object(node["_onFailure"]), path + ".onFailure", c) : null;
            var stripped = (JsonObject)node.DeepClone();
            foreach (var key in new[] { "_onSuccess", "_onFailure", "_skipIf", "_failureIf" }) stripped.Remove(key);
            ExperimentStep body = DecodeCore(stripped, path, c, depth);
            if (success is not null || failure is not null) body = new OutcomeMutationStep(new(path + ".hooks"), body, success, failure);
            if (node.ContainsKey("_skipIf")) body = new IfElseStep(new(path + ".skipIf"), BlackboardCondition(Object(node["_skipIf"]), c, "eq"), new DelayStep(new(path + ".skipped"), TimeSpan.Zero), body);
            if (node.ContainsKey("_failureIf")) body = new IfElseStep(new(path + ".failureIf"), BlackboardCondition(Object(node["_failureIf"]), c, "eq"),
                new ResultDecoratorStep(new(path + ".precondition_failure"), ResultEffect.ForceFailure, new DelayStep(new(path + ".rejected"), TimeSpan.Zero)), body);
            return body;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException or InvalidOperationException)
        { throw new FormatException($"PPMS node {path}" + (diagnosticLine is { } line ? $" (source line {line})" : "") + $": {error.Message}", error); }
    }
    private static ExperimentStep DecodeCore(JsonObject node, string path, ImportContext c, int depth)
    {
        try
        {
            if (depth > 128 || ++c.Nodes > 10_000) throw new FormatException("PPMS tree exceeds depth/node limits.");
            Fields(node, "type", "name", "label", "id", "source_line", "params", "children", "experiment_id");
            if (node.ContainsKey("experiment_id") && depth != 0) throw new FormatException("experiment_id is only valid at the root.");
            if (node.ContainsKey("source_line"))
            {
                var line = Number(node, "source_line");
                if (line < 1 || line != Math.Truncate(line)) throw new FormatException("source_line must be a positive integer.");
                c.Metadata[path + ".source_line"] = new(line, PhysicalUnit.Dimensionless);
            }
            var type = Canonical(Text(node, "type", "measurement"));
            var p = node.ContainsKey("params") ? Object(node["params"]) : new JsonObject();
            var id = new StepId(path);
            switch (type)
            {
                case "sequence":
                    Fields(p); return new SequenceStep(id, Children(node, path, c, depth));
                case "if_then_else":
                    if (p.ContainsKey("variable")) return NumericBranch(p, node, path, c, depth);
                    Fields(p); var branchChildren = Children(node, path, c, depth);
                    if (branchChildren.Count is not (2 or 3)) throw new FormatException("if_then_else node mode requires a condition, Then and optional Else.");
                    return new BranchOnOutcomeStep(id, branchChildren[0], branchChildren[1], branchChildren.Count == 3 ? branchChildren[2] : null);
                case "fallback":
                    Fields(p); return new FallbackStep(id, Children(node, path, c, depth));
                case "switch":
                    return ThresholdSwitch(p, node, path, c, depth);
                case "retry":
                    Fields(p, "max_attempts", "delay_ms");
                    var delay = Number(p, "delay_ms", 0);
                    if (delay < 0 || delay > uint.MaxValue - 1 || delay != Math.Truncate(delay)) throw new FormatException("delay_ms must be an integer within the supported timer range.");
                    var retryChildren = Children(node, path, c, depth);
                    if (retryChildren.Count != 1) throw new FormatException("retry requires exactly one child.");
                    return new RetryStep(id, Integer(p, "max_attempts", 3), TimeSpan.FromMilliseconds(delay), retryChildren[0]);
                case "timeout":
                    Fields(p, "timeout_s"); var timeoutChildren = Children(node, path, c, depth);
                    if (timeoutChildren.Count != 1) throw new FormatException("timeout requires exactly one child.");
                    return new TimeoutStep(id, Duration(p, "timeout_s", 60), timeoutChildren[0]);
                case "subtree":
                    Fields(p, "inputs", "outputs"); var scopeMappings = new List<VariableBinding>[] { [], [] };
                    foreach (var (name, index) in new[] { ("inputs", 0), ("outputs", 1) })
                        if (p.ContainsKey(name))
                            foreach (var entry in p[name] as JsonObject ?? throw new FormatException($"{name} must be an object."))
                            {
                                if (entry.Value is not JsonValue v || !v.TryGetValue<string>(out var target)) throw new FormatException($"{name} mapping must be a string.");
                                scopeMappings[index].Add(new(new(entry.Key), new(target)));
                            }
                    return new SubtreeStep(id, Children(node, path, c, depth), scopeMappings[0], scopeMappings[1]);
                case "force_success":
                case "force_failure":
                case "inverter":
                    Fields(p); var decoratorChildren = Children(node, path, c, depth);
                    if (decoratorChildren.Count != 1) throw new FormatException($"{type} requires exactly one child.");
                    return new ResultDecoratorStep(id, type == "force_success" ? ResultEffect.ForceSuccess : type == "force_failure" ? ResultEffect.ForceFailure : ResultEffect.Invert, decoratorChildren[0]);
                case "repeat":
                    Fields(p, "num_cycles"); var children = Children(node, path, c, depth);
                    if (children.Count != 1) throw new FormatException("repeat requires exactly one child.");
                    return new RepeatStep(id, Integer(p, "num_cycles", 3), children[0]);
                case "while":
                    return new WhileStep(id, BlackboardCondition(p, c, "lt"), new SequenceStep(new(path + ".body"), Children(node, path, c, depth)));
                case "for_each":
                    return PureIterator(p, node, path, c, depth);
                case "delay":
                    Fields(p, "delay_s"); return new SequenceStep(id, [new DelayStep(new(path + ".wait"), Duration(p, "delay_s", 1)), .. Children(node, path, c, depth, required: false)]);
                case "command":
                    NoChildren(node); return Command(Canonical(Text(node, "name")), p, path, c);
                case "measurement":
                    NoChildren(node); var measurement = Canonical(Text(p, "meas_type", Text(node, "name", "")));
                    return Measurement(measurement == "basic" ? Canonical("measure_" + Text(p, "name")) : measurement, p, path, c);
                case "scan":
                    return Scan(Canonical(Text(node, "name")), p, node, path, c, depth);
                default: throw new NotSupportedException($"Node type '{type}' has no migrated executor.");
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException or InvalidOperationException)
        { throw new FormatException($"PPMS node {path}" + (c.Metadata.TryGetValue(path + ".source_line", out var line) ? $" (source line {line.Value})" : "") + $": {error.Message}", error); }
    }
    private static List<ExperimentStep> Children(JsonObject node, string path, ImportContext c, int depth, bool required = true)
    {
        if (!node.ContainsKey("children") && !required) return [];
        var list = node["children"] as JsonArray ?? throw new FormatException("children must be an array.");
        return list.Select((child, index) => Decode(Object(child), path + "." + index, c, depth + 1)).ToList();
    }
    private static void NoChildren(JsonObject node)
    {
        if (node.ContainsKey("children") && (node["children"] is not JsonArray a || a.Count != 0)) throw new FormatException("Leaf nodes cannot have children.");
    }
    private static string Canonical(string name) => name.Trim().ToLowerInvariant() switch
    {
        "set_temp" or "temperature_set" or "temp_set" => "set_temperature",
        "field_set" => "set_field",
        "angle_set" or "rotator.set_angle" => "set_angle",
        "angle_zero" or "rotator.zero" => "zero_angle",
        "voltage_set" => "set_voltage",
        "bias_set" => "set_bias",
        "gate_set" => "set_gate",
        "current_set" => "set_current",
        "wait_temperature" or "wait_temp_state" or "wait_temp_stable" => "wait_temp",
        "wait_angle_state" => "wait_angle",
        "wait_field_holding" or "wait_field_stable" => "wait_field",
        "sleep" => "wait",
        "wait_before" => "delay",
        "loop" or "repeat_n" => "repeat",
        "with_timeout" => "timeout",
        "scope" or "isolated" => "subtree",
        "conditional" or "branch" => "if_then_else",
        "select_case" => "switch",
        "iterate" => "for_each",
        "then" or "else" => "sequence",
        "gate" or "bias" or "scan_bias" => "scan_gate",
        "scan_temp" => "scan_temperature",
        "time_s" => "scan_time",
        "field" => "scan_field",
        "angle" => "scan_angle",
        "resistance" or "measure_r" or "meas_resistance" or "measure_resistance" or "res" or "meas_res" or "meas-res" or "rxx" or "rxx_rxy" => "meas_r",
        "measure_r_sample" or "sample_r" => "meas_r_sample",
        "sample_r_2w" or "two_wire" => "meas_r_2w",
        "sample_r_4w" or "four_wire" => "meas_r_4w",
        "sample_r_hallbar" or "hallbar" or "rxx_rxy_sample" => "meas_r_hallbar",
        "sample_r_vdp" or "vdp" or "van_der_pauw" => "meas_r_vdp",
        "meas_temp" or "read_temp" or "temperature" => "measure_temp",
        "meas_field" or "read_field" => "measure_field",
        "meas_angle" or "read_angle" => "measure_angle",
        "pol_voltage" or "scan_pol_voltage" or "polarization_sweep" or "measure_polarization_sweep" => "measure_polarization",
        "pol_readout" or "polarization_readout" or "polread" => "measure_polarization_readout",
        "meas_current" or "read_current" => "measure_current",
        "meas_voltage" or "read_voltage" => "measure_voltage",
        _ => name.Trim().ToLowerInvariant(),
    };
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw new FormatException("Expected a JSON object.");
    private static void Fields(JsonObject value, params string[] fields)
    {
        foreach (var entry in value) if (!fields.Contains(entry.Key, StringComparer.Ordinal)) throw new NotSupportedException($"Parameter '{entry.Key}' is not migrated here.");
    }
    private static string Text(JsonObject value, string key, string? fallback = null)
    {
        if (!value.ContainsKey(key) && fallback is not null) return fallback;
        if (value[key] is JsonValue v && v.TryGetValue<string>(out var text)) return text;
        throw new FormatException($"{key} must be text.");
    }
    private static double Number(JsonObject value, string key, double? fallback = null)
    {
        if (!value.ContainsKey(key) && fallback is { } number) return number;
        if (value[key] is JsonValue v && v.TryGetValue<double>(out var result) && double.IsFinite(result)) return result;
        throw new FormatException($"{key} must be a finite number.");
    }
    private static int Integer(JsonObject value, string key, int? fallback = null)
    {
        var n = Number(value, key, fallback);
        if (n < 1 || n > 1_000_000 || n != Math.Truncate(n)) throw new FormatException($"{key} must be an integer in 1..1,000,000.");
        return (int)n;
    }
    private static bool Boolean(JsonObject value, string key, bool fallback)
    {
        if (!value.ContainsKey(key)) return fallback;
        if (value[key] is JsonValue v && v.TryGetValue<bool>(out var result)) return result;
        throw new FormatException($"{key} must be boolean.");
    }
    private static TimeSpan Duration(JsonObject value, string key, double fallback)
    {
        var seconds = Number(value, key, fallback);
        if (seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0) throw new FormatException($"{key} exceeds the supported timer range.");
        return TimeSpan.FromSeconds(seconds);
    }
    private static void Choice(JsonObject p, string key, string fallback, params string[] allowed)
    {
        var value = Text(p, key, fallback).Trim().ToLowerInvariant();
        if (!allowed.Contains(value, StringComparer.Ordinal)) throw new NotSupportedException($"{key}='{value}' is not migrated.");
    }
}

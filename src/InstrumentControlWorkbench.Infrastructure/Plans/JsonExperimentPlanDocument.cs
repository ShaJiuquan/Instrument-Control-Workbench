using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed partial class JsonExperimentPlanDocument : IExperimentPlanDocument
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private const string Format = "instrument-workbench-plan";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 512, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Serialize(ExperimentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CheckEnvelope(plan);
        var count = 0;
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = ExperimentPlan.FormatVersion,
            ["name"] = plan.Name,
            ["preparations"] = new JsonArray(plan.Preparations.Select(EncodePreparation).ToArray()),
            ["metadata"] = new JsonObject(plan.Metadata.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, EncodeQuantity(p.Value)))),
            ["root"] = EncodeStep(plan.Root, 0, ref count),
        };
        if (plan.Routing is { } routing) root["routing"] = EncodeRouting(routing);
        if (plan.MaximumStepExecutions != ExperimentPlan.MaximumSupportedStepExecutions) root["maximumStepExecutions"] = plan.MaximumStepExecutions;
        if (plan.CurrentSourcePreparations.Count != 0) root["currentSourcePreparations"] = new JsonArray(plan.CurrentSourcePreparations.Select(p =>
        {
            var item = new JsonObject { ["device"] = p.Device.Value, ["current"] = EncodeQuantity(p.Settings.Current), ["compliance"] = EncodeQuantity(p.Settings.Compliance), ["manualRange"] = p.Settings.ManualRange is { } range ? EncodeQuantity(range) : null };
            if (p.Settings.SourceDelay is { } delay) item["sourceDelay"] = EncodeQuantity(delay);
            return (JsonNode)item;
        }).ToArray());
        var text = root.ToJsonString(JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(plan), "Plan document exceeds 4 MiB.");
        return text;
    }

    public ExperimentPlan Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(text), "Plan document exceeds 4 MiB.");
        var root = Object(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 512 }),
            "format", "version", "name", "root", "preparations", "metadata", "routing", "currentSourcePreparations", "maximumStepExecutions");
        if (Text(root, "format") != Format) throw new FormatException("This is not an Instrument Workbench plan document.");
        if (Integer(root, "version") != ExperimentPlan.FormatVersion) throw new NotSupportedException("Unsupported plan version; the file was not changed.");
        var count = 0;
        var step = DecodeStep(Required(root, "root"), 0, ref count);
        var preparations = Array(Required(root, "preparations")).Select(DecodePreparation).ToArray();
        var metadata = Required(root, "metadata") as JsonObject ?? throw new FormatException("Metadata must be an object.");
        return new(Text(root, "name"), step, preparations, metadata.ToDictionary(p => p.Key, p => DecodeQuantity(p.Value)),
            root.ContainsKey("routing") ? DecodeRouting(Required(root, "routing")) : null,
            root.ContainsKey("currentSourcePreparations") ? Array(Required(root, "currentSourcePreparations")).Select(n =>
            {
                var p = Object(n, "device", "current", "compliance", "manualRange", "sourceDelay");
                return new CurrentSourcePreparation(new(Text(p, "device")), new(DecodeQuantity(Required(p, "current")), DecodeQuantity(Required(p, "compliance")), p["manualRange"] is null ? null : DecodeQuantity(p["manualRange"]), p["sourceDelay"] is null ? null : DecodeQuantity(p["sourceDelay"])));
            }) : null, root.ContainsKey("maximumStepExecutions") ? Integer(root, "maximumStepExecutions") : ExperimentPlan.MaximumSupportedStepExecutions);
    }

    public async Task<ExperimentPlan> LoadAsync(string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new IOException("Plan document exceeds 4 MiB.");
        // Bound reads even if another writer grows the file after the length check.
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + length > MaximumBytes) throw new IOException("Plan document exceeds 4 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, length), token);
        }
        token.ThrowIfCancellationRequested();
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return Parse(text);
    }

    public async Task SaveAsync(string path, ExperimentPlan plan, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        var text = Serialize(plan);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CheckEnvelope(ExperimentPlan plan)
    {
        if (plan.Preparations.Count + plan.CurrentSourcePreparations.Count > 10_000 || plan.Metadata.Count > 10_000)
            throw new FormatException("Too many preparation or metadata entries.");
        var pending = new Stack<(ExperimentStep Step, int Depth)>();
        pending.Push((plan.Root, 0));
        var nodes = 0;
        long values = 0;
        while (pending.TryPop(out var entry))
        {
            CheckSize(entry.Depth, ref nodes);
            values += entry.Step switch
            {
                AssignValueStep assignment => ValueCost(assignment.Value),
                SetParameterStep set => ValueCost(set.Value),
                ConditionStep condition => ValueCost(condition.Condition.Value),
                IfElseStep choice => ValueCost(choice.Condition.Value),
                WhileStep loop => ValueCost(loop.Condition.Value),
                SoftwareTemperatureRampStep ramp => ValueCost(ramp.Target),
                HardwareTemperatureSetpointStep ramp => ValueCost(ramp.Target),
                WaitTemperatureStep wait => ValueCost(wait.Target),
                WaitStableStep wait => ValueCost(wait.Target),
                ReadCurrentResistanceStep { ExpectedProgrammedCurrent: { } expected } => ValueCost(expected),
                _ => 0,
            };
            switch (entry.Step)
            {
                case MeasurementGroupStep point: foreach (var member in point.Members) pending.Push((member.Body, entry.Depth + 1)); break;
                case SampleAnalysisStep sample: foreach (var reading in sample.Readings) pending.Push((reading, entry.Depth + 1)); break;
                case FallbackStep fallback: foreach (var child in fallback.Children) pending.Push((child, entry.Depth + 1)); break;
                case RetryStep retry: pending.Push((retry.Body, entry.Depth + 1)); break;
                case WhileStep loop: pending.Push((loop.Body, entry.Depth + 1)); break;
                case OutcomeMutationStep hook:
                    pending.Push((hook.Body, entry.Depth + 1));
                    if (hook.Success is { } successHook) pending.Push((successHook, entry.Depth + 1));
                    if (hook.Failure is { } failureHook) pending.Push((failureHook, entry.Depth + 1)); break;
                case TimeoutStep timeout: pending.Push((timeout.Body, entry.Depth + 1)); break;
                case SubtreeStep subtree: values += subtree.Inputs.Count + subtree.Outputs.Count; foreach (var child in subtree.Children) pending.Push((child, entry.Depth + 1)); break;
                case BranchOnOutcomeStep branch:
                    pending.Push((branch.Condition, entry.Depth + 1)); pending.Push((branch.Then, entry.Depth + 1));
                    if (branch.Else is { } otherBranch) pending.Push((otherBranch, entry.Depth + 1)); break;
                case ResultDecoratorStep decorator: pending.Push((decorator.Body, entry.Depth + 1)); break;
                case SequenceStep sequence:
                    foreach (var child in sequence.Children) pending.Push((child, entry.Depth + 1)); break;
                case RepeatStep repeat: pending.Push((repeat.Body, entry.Depth + 1)); break;
                case ScanStep scan: values += scan.Values.Values.Count; pending.Push((scan.Body, entry.Depth + 1)); break;
                case ForEachStep loop: values += loop.Values.Values.Count; pending.Push((loop.Body, entry.Depth + 1)); break;
                case ReadStep read: values += read.Channels.Count + 20L * read.EnvironmentReads.Count; break;
                case ReadSourceSenseStep read: values += 20L * read.EnvironmentReads.Count; break;
                case ReadResistanceStep read: values += 20L * read.EnvironmentReads.Count; break;
                case ReadSampleResistanceStep read: values += 20L * read.Acquisition.EnvironmentReads.Count; break;
                case RouteMatrixStep route: values += 14L * route.Closed.Count; break;
                case IfElseStep conditional:
                    pending.Push((conditional.Then, entry.Depth + 1));
                    if (conditional.Else is { } otherwise) pending.Push((otherwise, entry.Depth + 1)); break;
            }
            // Bound DOM allocation before encoding, including shared scan arrays
            // and repeated text literals. The final UTF-8 byte check remains exact.
            if (values > MaximumBytes / 2) throw new FormatException("Plan values exceed the document size limit.");
        }
    }

    private static long ValueCost(PlanValue value) => value is TextValue text ? Math.Max(1, (text.Value.Length + 1L) / 2) : 1;

    private static JsonObject EncodeQuantity(Quantity q) => new() { ["value"] = q.Value, ["unit"] = q.Unit.ToString() };
    private static Quantity DecodeQuantity(JsonNode? node)
    {
        var o = Object(node, "value", "unit");
        return new(Number(o, "value"), NamedEnum<PhysicalUnit>(o, "unit"));
    }
    private static JsonObject EncodeAddress(ParameterAddress a) => new() { ["device"] = a.Device.Value, ["parameter"] = a.Parameter.Value };
    private static ParameterAddress DecodeAddress(JsonNode? node)
    {
        var o = Object(node, "device", "parameter");
        return new(new(Text(o, "device")), new(Text(o, "parameter")));
    }
    private static JsonObject EncodeValue(PlanValue v) => v switch
    {
        ConstantValue c => new() { ["constant"] = EncodeQuantity(c.Value) },
        BooleanValue b => new() { ["boolean"] = b.Value },
        TextValue t => new() { ["text"] = t.Value },
        VariableValue variable => new() { ["variable"] = variable.Variable.Value },
        QuantityFromNumberValue qualified => new() { ["quantityFromNumber"] = new JsonObject { ["variable"] = qualified.Variable.Value, ["unit"] = qualified.Unit.ToString() } },
        _ => throw new NotSupportedException($"No document codec exists for {v.GetType().Name}."),
    };
    private static PlanValue DecodeValue(JsonNode? node)
    {
        JsonObject o;
        try { o = Object(node, "constant", "variable", "quantityFromNumber", "boolean", "text"); }
        catch (ArgumentException error) { throw new FormatException("Invalid value expression fields.", error); }
        if (o.Count != 1) throw new FormatException("A value must contain exactly one value expression.");
        if (o.ContainsKey("boolean") || o.ContainsKey("text"))
        {
            try { return o.ContainsKey("boolean") ? new BooleanValue(Boolean(o, "boolean")) : new TextValue(Text(o, "text")); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { throw new FormatException("Invalid boolean or text literal.", error); }
        }
        if (o.ContainsKey("quantityFromNumber"))
        {
            var qualified = Object(Required(o, "quantityFromNumber"), "variable", "unit");
            return new QuantityFromNumberValue(new(Text(qualified, "variable")), NamedEnum<PhysicalUnit>(qualified, "unit"));
        }
        return o.ContainsKey("constant") ? new ConstantValue(DecodeQuantity(o["constant"])) : new VariableValue(new(Text(o, "variable")));
    }
    private static JsonObject EncodeValues(ScanValues values) => new()
    {
        ["unit"] = values.Unit.ToString(),
        ["values"] = new JsonArray(values.Values.Select(q => JsonValue.Create(q.Value)).ToArray<JsonNode?>()),
    };
    private static ScanValues DecodeValues(JsonNode? node)
    {
        var o = Object(node, "unit", "values");
        var unit = NamedEnum<PhysicalUnit>(o, "unit");
        return new(Array(Required(o, "values")).Select(v => new Quantity(v?.GetValue<double>() ?? throw new FormatException("Missing scan value."), unit)));
    }
    private static JsonObject EncodePreparation(SourcePreparation p)
    {
        var result = new JsonObject
        {
            ["device"] = p.Device.Value,
            ["function"] = p.Function.ToString(),
            ["level"] = EncodeQuantity(p.Level),
            ["compliance"] = EncodeQuantity(p.Compliance),
            ["sense"] = new JsonObject
            {
                ["function"] = p.Sense.Function.ToString(),
                ["mode"] = p.Sense.Mode.ToString(),
                ["nplc"] = p.Sense.Nplc,
                ["autoRange"] = p.Sense.AutoRange
            },
        };
        if (p.Sense.Acquisition is { } acquisition) ((JsonObject)result["sense"]!)["acquisition"] = new JsonObject
        {
            ["autoZero"] = acquisition.AutoZero,
            ["autorangeRebound"] = acquisition.AutorangeRebound,
            ["averagingEnabled"] = acquisition.AveragingEnabled,
            ["averagingCount"] = acquisition.AveragingCount,
            ["averagingMode"] = acquisition.AveragingMode.ToString()
        };
        if (p.Sense.ManualRange is { } senseRange) ((JsonObject)result["sense"]!)["manualRange"] = EncodeQuantity(senseRange);
        if (p.ManualSourceRange is { } range) result["manualSourceRange"] = EncodeQuantity(range);
        return result;
    }
    private static SourcePreparation DecodePreparation(JsonNode? node)
    {
        var o = Object(node, "device", "function", "level", "compliance", "sense", "manualSourceRange");
        var sense = Object(Required(o, "sense"), "function", "mode", "nplc", "autoRange", "manualRange", "acquisition");
        Keithley2450AcquisitionSettings? acquisition = null;
        if (sense["acquisition"] is { } configured)
        {
            var settings = Object(configured, "autoZero", "autorangeRebound", "averagingEnabled", "averagingCount", "averagingMode");
            acquisition = new(Boolean(settings, "autoZero"), Boolean(settings, "autorangeRebound"), Boolean(settings, "averagingEnabled"), Integer(settings, "averagingCount"), NamedEnum<AveragingMode>(settings, "averagingMode"));
        }
        return new(new(Text(o, "device")), NamedEnum<SourceFunction>(o, "function"), DecodeQuantity(o["level"]), DecodeQuantity(o["compliance"]),
            new(NamedEnum<SenseFunction>(sense, "function"), NamedEnum<SensingMode>(sense, "mode"), Number(sense, "nplc"), Boolean(sense, "autoRange"), sense["manualRange"] is null ? null : DecodeQuantity(sense["manualRange"]), acquisition), o["manualSourceRange"] is null ? null : DecodeQuantity(o["manualSourceRange"]));
    }
    private static JsonObject Object(JsonNode? node, params string[] allowed)
    {
        var result = node as JsonObject ?? throw new FormatException("Expected a JSON object.");
        foreach (var item in result)
            if (!allowed.Contains(item.Key, StringComparer.Ordinal)) throw new FormatException($"Unknown plan field '{item.Key}'.");
        return result;
    }
    private static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw new FormatException("Expected an array.");
    private static JsonNode Required(JsonObject o, string name) => o[name] ?? throw new FormatException($"Missing plan field '{name}'.");
    private static string Text(JsonObject o, string name) => Required(o, name).GetValue<string>();
    private static double Number(JsonObject o, string name) => Required(o, name).GetValue<double>();
    private static int Integer(JsonObject o, string name) => Required(o, name).GetValue<int>();
    private static bool Boolean(JsonObject o, string name) => Required(o, name).GetValue<bool>();
    private static T NamedEnum<T>(JsonObject o, string name) where T : struct, Enum
    {
        var text = Text(o, name);
        if (!Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)) throw new FormatException($"Unknown {name} '{text}'.");
        return Enum.Parse<T>(text);
    }
    private static TimeSpan Duration(JsonObject o, string name)
    {
        var seconds = Number(o, name);
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0)
            throw new FormatException($"{name} is outside the supported timer range.");
        return TimeSpan.FromSeconds(seconds);
    }
    private static void CheckSize(int depth, ref int count)
    {
        if (depth > 128 || ++count > 10_000) throw new FormatException("Plan exceeds the supported depth or node count.");
    }
}

using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed class JsonRoutingProfileDocument : IRoutingProfileDocument
{
    public const int MaximumBytes = 12 * 1024 * 1024;
    private const string Format = "instrument-workbench-routing";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 32 };
    public string Serialize(RoutingProfileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        long bytes = 0;
        void TextBudget(params string[] text)
        {
            foreach (var value in text) bytes += Encoding.UTF8.GetByteCount(value);
            if (bytes > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(catalog), "Routing text exceeds 12 MiB.");
        }
        foreach (var c in catalog.Channels)
        {
            TextBudget(c.Id.Value, c.Name, c.OriginalId, c.Alias, c.Notes);
            foreach (var connection in c.Connections) TextBudget(connection.Key.Value, connection.Label, connection.Raw, connection.Notes);
        }
        foreach (var s in catalog.Samples)
        {
            TextBudget(s.Id.Value, s.Name, s.OriginalId, s.Material, s.Substrate, s.Notes);
            foreach (var electrode in s.Electrodes) TextBudget(electrode.Label.Value, electrode.Channel.Value, electrode.Connection.Value, electrode.Notes);
        }
        foreach (var wiring in catalog.Wirings) TextBudget(wiring.Id.Value, wiring.Label);
        foreach (var mode in catalog.ModeWirings) TextBudget(mode.Key, mode.Value.Value);
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["activeChannel"] = catalog.ActiveChannel.Value,
            ["activeSample"] = catalog.ActiveSample.Value,
            ["channels"] = new JsonArray(catalog.Channels.Select(c => (JsonNode)JsonExperimentPlanDocument.EncodeChannel(c)).ToArray()),
            ["samples"] = new JsonArray(catalog.Samples.Select(s => (JsonNode)JsonExperimentPlanDocument.EncodeSample(s)).ToArray()),
            ["wirings"] = new JsonArray(catalog.Wirings.Select(w => (JsonNode)new JsonObject
            {
                ["id"] = w.Id.Value,
                ["label"] = w.Label,
                ["sourcePlus"] = Port(w.SourcePlus),
                ["sourceMinus"] = Port(w.SourceMinus),
                ["sensePlus"] = Port(w.SensePlus),
                ["senseMinus"] = Port(w.SenseMinus),
            }).ToArray()),
            ["modeWirings"] = new JsonObject(catalog.ModeWirings.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, JsonValue.Create(p.Value.Value)))),
        };
        var text = root.ToJsonString(Options);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(catalog), "Routing document exceeds 12 MiB.");
        return text;
    }
    public RoutingProfileCatalog Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new FormatException("Routing document exceeds 12 MiB.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var pending = new Stack<JsonElement>(); pending.Push(document.RootElement); var count = 1;
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in value.EnumerateObject())
                    {
                        if (!names.Add(p.Name)) throw new FormatException("Duplicate routing field " + p.Name);
                        if (++count > 100_000) throw new FormatException("Routing document exceeds 100000 JSON values.");
                        pending.Push(p.Value);
                    }
                }
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        if (++count > 100_000) throw new FormatException("Routing document exceeds 100000 JSON values.");
                        pending.Push(item);
                    }
                }
            }
            var root = Object(JsonNode.Parse(text), "format", "version", "activeChannel", "activeSample", "channels", "samples", "wirings", "modeWirings");
            if (root["format"]?.GetValue<string>() != Format || root["version"]?.GetValue<int>() != 1) throw new NotSupportedException("Unsupported native routing format/version.");
            var modes = Object(root["modeWirings"]);
            return new(new(Text(root, "activeChannel")), new(Text(root, "activeSample")),
                Array(root, "channels").Select(JsonExperimentPlanDocument.DecodeChannel), Array(root, "samples").Select(JsonExperimentPlanDocument.DecodeSample),
                Array(root, "wirings").Select(value =>
                {
                    var w = Object(value, "id", "label", "sourcePlus", "sourceMinus", "sensePlus", "senseMinus");
                    return new MatrixWiringLayout(new(Text(w, "id")), Text(w, "label"), Port(w["sourcePlus"]), Port(w["sourceMinus"]), Port(w["sensePlus"]), Port(w["senseMinus"]));
                }), modes.Select(p => KeyValuePair.Create(p.Key, new WiringProfileId(p.Value?.GetValue<string>() ?? throw new FormatException("Missing mode wiring key.")))));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new FormatException("Invalid native routing document: " + e.Message, e); }
    }
    public async Task<RoutingProfileCatalog> LoadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new IOException("Routing document exceeds 12 MiB.");
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > MaximumBytes) throw new IOException("Routing document grew beyond 12 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        token.ThrowIfCancellationRequested(); var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        return Parse(text.StartsWith('\uFEFF') ? text[1..] : text);
    }
    public async Task SaveAsync(string path, RoutingProfileCatalog catalog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var text = Serialize(catalog); token.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path); var directory = Path.GetDirectoryName(fullPath)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Exception? error = null;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            { await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token); await stream.FlushAsync(token); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static JsonObject Object(JsonNode? node, params string[] fields)
    {
        var value = node as JsonObject ?? throw new FormatException("Expected routing object.");
        if (fields.Length != 0 && value.Any(p => !fields.Contains(p.Key))) throw new FormatException("Unknown native routing field.");
        return value;
    }
    private static JsonArray Array(JsonObject node, string key)
    {
        var values = node[key] as JsonArray ?? throw new FormatException("Missing routing list " + key);
        if (values.Count is < 1 or > 64) throw new FormatException("Routing lists require one to 64 entries.");
        return values;
    }
    private static string Text(JsonObject node, string key) => node[key]?.GetValue<string>() ?? throw new FormatException("Missing routing field " + key);
    private static JsonObject Port(MatrixPort port) => new() { ["slot"] = port.Slot, ["row"] = port.Row };
    private static MatrixPort Port(JsonNode? value)
    {
        var p = Object(value, "slot", "row");
        return new(p["slot"]?.GetValue<int>() ?? throw new FormatException("Missing port slot."), p["row"]?.GetValue<int>() ?? throw new FormatException("Missing port row."));
    }
}

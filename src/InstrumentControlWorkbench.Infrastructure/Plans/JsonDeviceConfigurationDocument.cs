using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed class JsonDeviceConfigurationDocument : IDeviceConfigurationDocument
{
    public const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 16 };
    public string Serialize(DeviceConfigurationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Devices.Sum(device => 14L * (device.Matrix?.AcIsolation?.Connections.Count ?? 0)) > MaximumBytes / 2)
            throw new ArgumentOutOfRangeException(nameof(catalog), "Device isolation values exceed the document budget.");
        var root = new JsonObject
        {
            ["format"] = "instrument-workbench-devices",
            ["version"] = 1,
            ["primarySource"] = catalog.PrimarySource?.Value,
            ["devices"] = new JsonArray(catalog.Devices.Select(d =>
            {
                var value = new JsonObject
                {
                    ["id"] = d.Id.Value,
                    ["model"] = d.Model,
                    ["displayName"] = d.DisplayName,
                    ["category"] = d.Category.ToString(),
                    ["enabled"] = d.Enabled,
                    ["connection"] = EncodeConnection(d.Connection)
                };
                if (d.Matrix is { } m)
                {
                    var matrix = new JsonObject
                    {
                        ["cards"] = new JsonArray(m.Cards.Select(c => (JsonNode)new JsonObject { ["slot"] = c.Slot, ["rows"] = c.Rows, ["columns"] = c.Columns }).ToArray()),
                        ["guardedSources"] = new JsonArray(m.GuardedSources.Select(id => (JsonNode)JsonValue.Create(id.Value)!).ToArray()),
                    };
                    if (m.AcIsolation is { } isolation) matrix["acIsolation"] = new JsonObject
                    {
                        ["generator"] = isolation.Generator.Value,
                        ["connections"] = new JsonArray(isolation.Connections.Select(point => (JsonNode)new JsonObject { ["slot"] = point.Slot, ["row"] = point.Row, ["column"] = point.Column }).ToArray()),
                        ["maxAmplitude"] = EncodeQuantity(isolation.MaximumAmplitude),
                        ["maxFrequency"] = EncodeQuantity(isolation.MaximumFrequency),
                    };
                    value["matrix"] = matrix;
                }
                return (JsonNode)value;
            }).ToArray()),
            ["aliases"] = new JsonObject(catalog.Aliases.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, JsonValue.Create(p.Value.Value)))),
        };
        var text = root.ToJsonString(Options);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(catalog), "Device document exceeds 1 MiB.");
        return text;
    }
    public DeviceConfigurationCatalog Parse(string text)
    {
        try
        {
            var root = Object(ParseObject(text), "format", "version", "primarySource", "devices", "aliases");
            if (Text(root, "format") != "instrument-workbench-devices" || root["version"]?.GetValue<int>() != 1)
                throw new NotSupportedException("Unsupported device document format/version.");
            var devices = List(root["devices"]).Select(item =>
            {
                var d = Object(item, "id", "model", "displayName", "category", "enabled", "connection", "matrix");
                var name = Text(d, "category");
                if (!Enum.GetNames<ConfiguredDeviceCategory>().Contains(name)) throw new FormatException("Unknown device category.");
                ConfiguredMatrix? matrix = null;
                if (d.ContainsKey("matrix"))
                {
                    var m = Object(d["matrix"], "cards", "guardedSources", "acIsolation");
                    MatrixAcIsolationCapability? isolation = null;
                    if (m.ContainsKey("acIsolation"))
                    {
                        var capability = Object(m["acIsolation"], "generator", "connections", "maxAmplitude", "maxFrequency");
                        isolation = new(new(Text(capability, "generator")), List(capability["connections"], 1, 4096).Select(value =>
                        {
                            var point = Object(value, "slot", "row", "column");
                            return new MatrixCrossPoint(Integer(point, "slot"), Integer(point, "row"), Integer(point, "column"));
                        }), DecodeQuantity(capability["maxAmplitude"]), DecodeQuantity(capability["maxFrequency"]));
                    }
                    matrix = new(List(m["cards"]).Select(card =>
                    {
                        var c = Object(card, "slot", "rows", "columns");
                        return new MatrixCardDefinition(Integer(c, "slot"), Integer(c, "rows"), Integer(c, "columns"));
                    }), List(m["guardedSources"], 0).Select(source => new InstrumentId(source?.GetValue<string>() ?? throw new FormatException("Missing guarded source."))), isolation);
                }
                return new DeviceConfiguration(new(Text(d, "id")), Text(d, "model"), Text(d, "displayName"), Enum.Parse<ConfiguredDeviceCategory>(name),
                    DecodeConnection(d["connection"]), d["enabled"]?.GetValue<bool>() ?? throw new FormatException("Missing enabled flag."), matrix);
            });
            var aliases = Object(root["aliases"]);
            if (!root.ContainsKey("primarySource")) throw new FormatException("Missing primary source selection.");
            InstrumentId? primary = root["primarySource"] is null ? null : new InstrumentId(Text(root, "primarySource"));
            return new(primary, devices, aliases.Select(p => KeyValuePair.Create(p.Key, new InstrumentId(p.Value?.GetValue<string>() ?? throw new FormatException("Missing alias target.")))));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new FormatException("Invalid device document: " + e.Message, e); }
    }
    public async Task<DeviceConfigurationCatalog> LoadAsync(string path, CancellationToken token) => Parse(await ReadTextAsync(path, token));
    public async Task SaveAsync(string path, DeviceConfigurationCatalog catalog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var text = Serialize(catalog); token.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(path); var directory = Path.GetDirectoryName(full)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp"); Exception? error = null;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            { await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token); await stream.FlushAsync(token); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, full, overwrite: true);
        }
        catch (Exception e) { error = e; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) { error = error is null ? e : new AggregateException(error, e); }
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    internal static async Task<string> ReadTextAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new IOException("Device document exceeds 1 MiB.");
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > MaximumBytes) throw new IOException("Device document grew beyond 1 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        token.ThrowIfCancellationRequested(); var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }
    internal static JsonObject ParseObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new FormatException("Device document exceeds 1 MiB.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var pending = new Stack<JsonElement>(); pending.Push(document.RootElement); var count = 1;
        while (pending.TryPop(out var node))
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in node.EnumerateObject())
                {
                    if (!names.Add(p.Name)) throw new FormatException("Duplicate device field " + p.Name);
                    if (++count > 50_000) throw new FormatException("Device document exceeds 50000 values.");
                    pending.Push(p.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var value in node.EnumerateArray())
                { if (++count > 50_000) throw new FormatException("Device document exceeds 50000 values."); pending.Push(value); }
        }
        return Object(JsonNode.Parse(text));
    }
    internal static JsonObject Object(JsonNode? node, params string[] keys)
    {
        var value = node as JsonObject ?? throw new FormatException("Expected device object.");
        if (keys.Length != 0 && value.Any(p => !keys.Contains(p.Key))) throw new FormatException("Unknown device field.");
        return value;
    }
    private static JsonArray List(JsonNode? node, int minimum = 1, int maximum = 64)
    {
        var values = node as JsonArray ?? throw new FormatException("Expected device list.");
        if (values.Count < minimum || values.Count > maximum) throw new FormatException($"Device list requires {minimum} to {maximum} entries.");
        return values;
    }
    internal static string Text(JsonObject node, string key) => node[key]?.GetValue<string>() ?? throw new FormatException("Missing device field " + key);
    internal static int Integer(JsonObject node, string key) => node[key]?.GetValue<int>() ?? throw new FormatException("Missing device field " + key);
    private static JsonObject EncodeQuantity(Quantity value) => new() { ["value"] = value.Value, ["unit"] = value.Unit.ToString() };
    private static Quantity DecodeQuantity(JsonNode? node)
    {
        var quantity = Object(node, "value", "unit");
        var unit = Text(quantity, "unit");
        if (!Enum.GetNames<PhysicalUnit>().Contains(unit, StringComparer.Ordinal)) throw new FormatException("Unknown isolation quantity unit.");
        return new(quantity["value"]?.GetValue<double>() ?? throw new FormatException("Missing isolation quantity value."), Enum.Parse<PhysicalUnit>(unit));
    }
    private static JsonObject EncodeConnection(DeviceConnection connection) => connection switch
    {
        SimulationDeviceConnection => new() { ["transport"] = "simulation" },
        TcpDeviceConnection tcp => new() { ["transport"] = "tcp", ["host"] = tcp.Host, ["port"] = tcp.Port, ["terminator"] = tcp.Terminator, ["timeoutSeconds"] = (decimal)tcp.Timeout.Ticks / TimeSpan.TicksPerSecond },
        VisaDeviceConnection visa => new() { ["transport"] = "visa", ["resource"] = visa.Resource },
        SerialDeviceConnection serial => new() { ["transport"] = "serial", ["port"] = serial.Port, ["timeoutSeconds"] = (decimal)serial.Timeout.Ticks / TimeSpan.TicksPerSecond },
        _ => throw new NotSupportedException("Unknown connection type."),
    };
    private static DeviceConnection DecodeConnection(JsonNode? node)
    {
        var c = Object(node);
        switch (Text(c, "transport"))
        {
            case "simulation": Object(c, "transport"); return new SimulationDeviceConnection();
            case "tcp":
                Object(c, "transport", "host", "port", "terminator", "timeoutSeconds");
                return new TcpDeviceConnection(Text(c, "host"), Integer(c, "port"), Text(c, "terminator"), Timeout(c, "timeoutSeconds"));
            case "visa": Object(c, "transport", "resource"); return new VisaDeviceConnection(Text(c, "resource"));
            case "serial":
                Object(c, "transport", "port", "timeoutSeconds"); return new SerialDeviceConnection(Text(c, "port"), Timeout(c, "timeoutSeconds"));
            default: throw new NotSupportedException("Unsupported device transport.");
        }
    }
    internal static TimeSpan Timeout(JsonObject node, string key)
    {
        var seconds = node[key]?.GetValue<decimal>() ?? throw new FormatException("Missing timeout.");
        var ticks = seconds * TimeSpan.TicksPerSecond;
        if (ticks != decimal.Truncate(ticks)) throw new FormatException("Timeout precision exceeds runtime ticks.");
        return TimeSpan.FromTicks(checked((long)ticks));
    }
}

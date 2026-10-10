using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Infrastructure.Plans;

public sealed class PpmsRoutingProfileImporter : IRoutingProfileImporter
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public RoutingProfileCatalog Parse(string channels, string samples, string instrumentPorts)
    {
        var c = Document(channels, "channel_profiles.json", "active", "profiles");
        var s = Document(samples, "samples.json", "active", "samples");
        var w = Document(instrumentPorts, "instrument_ports.json", "wiring", "mode_wiring");
        try
        {
            var profiles = Entries(c, "profiles", Channel); var specimens = Entries(s, "samples", Sample);
            var layouts = Entries(w, "wiring", Wiring);
            var modes = Object(w["mode_wiring"]);
            return new(new(Text(c, "active")), new(Text(s, "active")), profiles, specimens, layouts,
                modes.Select(p => KeyValuePair.Create(p.Key, new WiringProfileId(p.Value?.GetValue<string>() ?? throw new FormatException("Mode wiring requires a string key.")))));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException or KeyNotFoundException)
        { throw new FormatException("PPMS routing profiles are invalid: " + e.Message, e); }
    }
    public async Task<RoutingProfileCatalog> LoadAsync(string channelsPath, string samplesPath, string instrumentPortsPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var channels = await ReadAsync(channelsPath, token);
        var samples = await ReadAsync(samplesPath, token);
        var ports = await ReadAsync(instrumentPortsPath, token);
        token.ThrowIfCancellationRequested();
        return Parse(channels, samples, ports);
    }
    private static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new IOException("Routing profile document exceeds 4 MiB.");
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + count > MaximumBytes) throw new IOException("Routing profile document exceeds 4 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, count), token);
        }
        var text = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }
    private static JsonObject Document(string text, string name, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new FormatException(name + " exceeds 4 MiB.");
        try
        {
            // Inspect duplicate property names before JsonNode materialization can obscure them.
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var pending = new Stack<JsonElement>(); pending.Push(document.RootElement); var nodes = 0;
            while (pending.TryPop(out var element))
            {
                if (++nodes > 10_000) throw new FormatException("Routing document exceeds the 10000-value budget.");
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new FormatException("Duplicate property: " + property.Name);
                        pending.Push(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) pending.Push(value);
            }
            return Object(JsonNode.Parse(text), fields);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        { throw new FormatException(name + ": " + e.Message, e); }
    }
    private static T[] Entries<T>(JsonObject root, string key, Func<string, JsonObject, T> read)
    {
        var entries = Object(root[key]);
        if (entries.Count is < 1 or > 64) throw new FormatException(key + " needs one to 64 profiles.");
        return entries.Select(p =>
        {
            try { return read(p.Key, Object(p.Value)); }
            catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or OverflowException)
            { throw new FormatException(key + "." + p.Key + ": " + e.Message, e); }
        }).ToArray();
    }
    private static ChannelProfile Channel(string key, JsonObject c)
    {
        Fields(c, "channel_id", "group_index", "name", "alias", "notes", "connections");
        var connections = Array(c, "connections").Select(v =>
        {
            var p = Object(v, "index", "key", "label", "route", "enabled", "notes");
            var r = Object(p["route"], "slot", "row", "col", "raw");
            return new ChannelConnection(new(Text(p, "key")), Integer(r, "slot", optional: true), Integer(r, "col", optional: true), Boolean(p, "enabled"),
                Integer(p, "index"), Text(p, "label"), Integer(r, "row", optional: true), Text(r, "raw", ""), Text(p, "notes", ""));
        });
        return new(new(key), Text(c, "name"), connections, Text(c, "channel_id"), Integer(c, "group_index"), Text(c, "alias", ""), Text(c, "notes", ""));
    }
    private static SampleProfile Sample(string key, JsonObject s)
    {
        Fields(s, "sample_id", "name", "topology", "material", "substrate", "notes", "geometry", "electrodes");
        var topology = Text(s, "topology") switch
        {
            "two_wire" => SampleTopology.TwoWire,
            "four_wire" => SampleTopology.FourWire,
            "hall_bar" => SampleTopology.HallBar,
            "van_der_pauw" => SampleTopology.VanDerPauw,
            _ => throw new FormatException("Unknown sample topology."),
        };
        var g = s.ContainsKey("geometry") ? Object(s["geometry"], "width_m", "length_m", "thickness_m", "spacing_m") : new JsonObject();
        var geometry = new SampleGeometry(Dimension(g, "width_m"), Dimension(g, "length_m"), Dimension(g, "thickness_m"), Dimension(g, "spacing_m"));
        var electrodes = Array(s, "electrodes").Select(v =>
        {
            var e = Object(v, "label", "link", "enabled", "notes"); var link = Object(e["link"], "channel", "connection");
            return new SampleElectrode(new(Text(e, "label")), new(Text(link, "channel")), new(Text(link, "connection")), Boolean(e, "enabled"), Text(e, "notes", ""));
        });
        return new(new(key), Text(s, "name"), topology, electrodes, geometry, Text(s, "sample_id"), Text(s, "material", ""), Text(s, "substrate", ""), Text(s, "notes", ""));
    }
    private static MatrixWiringLayout Wiring(string key, JsonObject w)
    {
        Fields(w, "label", "source_plus", "source_minus", "sense_plus", "sense_minus");
        return new(new(key), Text(w, "label", ""), Port(w, "source_plus"), Port(w, "source_minus"), Port(w, "sense_plus"), Port(w, "sense_minus"));
    }
    private static MatrixPort Port(JsonObject wiring, string key)
    {
        var p = Object(wiring[key], "slot", "row");
        return new(p.ContainsKey("slot") ? Integer(p, "slot")!.Value : 1, Integer(p, "row")!.Value);
    }
    private static Quantity? Dimension(JsonObject g, string key) => g[key] is { } value ? new(value.GetValue<double>(), PhysicalUnit.Meter) : null;
    private static string Text(JsonObject o, string key, string? fallback = null) => o[key]?.GetValue<string>() ??
        (fallback is not null && !o.ContainsKey(key) ? fallback : throw new FormatException("Missing text field " + key));
    private static int? Integer(JsonObject o, string key, bool optional = false) => o[key] is { } value ? value.GetValue<int>() :
        optional ? null : throw new FormatException("Missing integer field " + key);
    private static bool Boolean(JsonObject o, string key) => o.ContainsKey(key) ? o[key]?.GetValue<bool>() ?? throw new FormatException("Invalid boolean " + key) : true;
    private static JsonArray Array(JsonObject o, string key)
    {
        var result = o[key] as JsonArray ?? throw new FormatException(key + " must be an array.");
        if (result.Count is < 1 or > 64) throw new FormatException(key + " needs one to 64 entries.");
        return result;
    }
    private static JsonObject Object(JsonNode? value, params string[] fields)
    {
        var result = value as JsonObject ?? throw new FormatException("Expected an object.");
        if (fields.Length != 0) Fields(result, fields);
        return result;
    }
    private static void Fields(JsonObject o, params string[] fields)
    { foreach (var p in o) if (!fields.Contains(p.Key, StringComparer.Ordinal)) throw new FormatException("Unknown profile field " + p.Key); }
}

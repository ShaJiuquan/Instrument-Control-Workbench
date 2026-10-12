using System.Text.Json;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.Cli;

internal static class ImportPlanCommand
{
    public static async Task<ValidatedPlanDocument> ExecuteAsync(CliOptions options, ExperimentPlanWorkspace workspace, CancellationToken token)
    {
        if (options.Ppms is null || options.Output is null) throw new ArgumentException("Import requires --ppms and --output.");
        var context = await workspace.LoadAsync(options.Plan, token);
        ImportRoutingSelection? routing = null;
        if (options.Routing is not null)
        {
            var fullPath = Path.GetFullPath(options.Routing); var directory = Path.GetDirectoryName(fullPath)!;
            using var json = JsonDocument.Parse(await ReadBoundedAsync(fullPath, token), new() { MaxDepth = 16 }); var root = json.RootElement;
            Fields(root, "format", "version", "channels", "samples", "ports", "matrix", "channel_wiring", "route_settle_s", "wirings");
            if (Text(root, "format") != "instrument-workbench-import-routing" || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value != 1) throw new JsonException("Routing selection requires format instrument-workbench-import-routing and version 1.");
            var channels = Path.GetFullPath(Text(root, "channels"), directory); var samples = Path.GetFullPath(Text(root, "samples"), directory); var ports = Path.GetFullPath(Text(root, "ports"), directory);
            var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (new[] { channels, samples, ports }.Any(path => comparison.Equals(Path.GetFullPath(options.Output), path))) throw new ArgumentException("Import output cannot overwrite an input routing profile.");
            var profiles = await workspace.LoadRoutingProfilesAsync(channels, samples, ports, token);
            if (!root.TryGetProperty("wirings", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 64) throw new JsonException("Choose one to 64 explicit wirings.");
            var wirings = items.EnumerateArray().Select(item =>
            {
                Fields(item, "id", "source", "voltage_meter");
                return new ImportWiringSelection(new(Text(item, "id")), new(Text(item, "source")), item.TryGetProperty("voltage_meter", out _) ? new InstrumentId(Text(item, "voltage_meter")) : null);
            }).ToArray();
            var seconds = root.TryGetProperty("route_settle_s", out var settling) && settling.TryGetDouble(out var number) ? number : throw new JsonException("route_settle_s is required and numeric.");
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalSeconds) throw new ArgumentException("Route settling is outside the supported timer range.");
            routing = new(profiles, new(Text(root, "matrix")), wirings, new(Text(root, "channel_wiring")), TimeSpan.FromSeconds(seconds));
        }
        var bindings = workspace.CreateConfiguredImportBindings(context.Plan, routing);
        var imported = await workspace.ImportAsync(options.Ppms, bindings, token, context.Plan.MaximumStepExecutions);
        token.ThrowIfCancellationRequested();
        return await workspace.SaveAsync(options.Output, imported.Text, token);
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new JsonException(name + " must be a nonempty string.");
    private static void Fields(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Routing selection entries must be objects.");
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => !allowed.Contains(name, StringComparer.Ordinal))) throw new JsonException("Unknown or duplicate routing selection fields.");
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        var buffer = new byte[65537]; var count = 0;
        while (count < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(count), token); if (read == 0) break; count += read; }
        if (count > 65536) throw new JsonException("Routing selection exceeds 64 KiB.");
        return buffer[..count];
    }
}

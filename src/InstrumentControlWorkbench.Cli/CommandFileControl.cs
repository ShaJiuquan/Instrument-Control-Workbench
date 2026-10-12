using System.Security.Cryptography;
using System.Text.Json;
using InstrumentControlWorkbench.Application.Measurements;

namespace InstrumentControlWorkbench.Cli;

internal sealed class CommandFileControl(string path, MeasurementRunService runs, Func<object, Task> report)
{
    private const int MaximumRequests = 4096;
    private readonly HashSet<Guid> requests = [];
    private string? lastHash;
    private string? lastReadError;
    public async Task PollAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path))
                {
                    var bytes = await ReadBoundedAsync(path, token);
                    lastReadError = null;
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (hash != lastHash)
                    {
                        lastHash = hash;
                        try { await ApplyAsync(bytes, token); }
                        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                        { await report(new { version = 1, kind = "control_rejected", error = ex.Message, phase = runs.Phase.ToString(), ownership_retained = runs.HasActiveRun }); }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (lastReadError != ex.Message)
                {
                    lastReadError = ex.Message;
                    await report(new { version = 1, kind = "control_rejected", error = ex.Message, phase = runs.Phase.ToString(), ownership_retained = runs.HasActiveRun });
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        }
    }
    private async Task ApplyAsync(byte[] bytes, CancellationToken token)
    {
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 8 }); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Control must be an object.");
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => name is not ("version" or "request_id" or "run_id" or "command" or "reconnect"))) throw new JsonException("Duplicate or unknown control field.");
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
            !Guid.TryParse(Text(root, "request_id"), out var request) || request == Guid.Empty) throw new JsonException("Control requires version 1 and a nonempty UUID request_id.");
        var command = Text(root, "command");
        if (requests.Contains(request)) throw new InvalidOperationException("request_id was already processed; use a new ID.");
        if (command != "status")
        {
            if (requests.Count >= MaximumRequests && command is not ("abort" or "recover")) throw new InvalidOperationException("Control request capacity reached; only status, abort and recovery remain available.");
            if (requests.Count < MaximumRequests) requests.Add(request);
        }
        var run = runs.LatestDataSet?.Id.ToString();
        if (run is null || Text(root, "run_id") != run) throw new InvalidOperationException("Control run_id must match this process's acquired run.");
        var reconnect = root.TryGetProperty("reconnect", out var reconnectValue) ? reconnectValue.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException("reconnect must be Boolean.") } : false;
        if (root.TryGetProperty("reconnect", out _) && command != "recover") throw new JsonException("reconnect applies only to recover.");
        switch (command)
        {
            case "status": break;
            case "abort": if (!runs.HasActiveRun) throw new InvalidOperationException("No owned run remains."); runs.Abort(); break;
            case "pause": runs.Pause(); break;
            case "resume": runs.Resume(); break;
            case "recover": await runs.RecoverSafetyAsync(token, reconnect); break;
            default: throw new ArgumentException("command must be status, abort, pause, resume or recover.");
        }
        await report(new { version = 1, kind = "control_applied", request_id = request, run_id = run, command, phase = runs.Phase.ToString(), ownership_retained = runs.HasActiveRun, rows = runs.LatestDataSet?.TotalRowCount });
    }
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException(name + " must be a string.");
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var bytes = new byte[8193]; var count = 0;
        while (count < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(count), token); if (read == 0) break; count += read; }
        if (count > 8192) throw new IOException("Control file exceeds 8192 bytes.");
        return bytes[..count];
    }
}

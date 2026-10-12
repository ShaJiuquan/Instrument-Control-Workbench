using System.Runtime.InteropServices;
using System.Text.Json;

namespace InstrumentControlWorkbench.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h") { Console.WriteLine(CliOptions.Usage); return 0; }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupted = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += interrupted;
        using var termination = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; cancellation.Cancel(); }) : null;
        try { return await new CommandLineRunner().ExecuteAsync(CliOptions.Parse(args), Console.Out, cancellation.Token); }
        catch (Exception ex) { Console.Error.WriteLine(JsonSerializer.Serialize(new { version = 1, kind = "error", error = ex.Message })); return ex is OperationCanceledException ? 130 : 2; }
        finally { Console.CancelKeyPress -= interrupted; }
    }
}

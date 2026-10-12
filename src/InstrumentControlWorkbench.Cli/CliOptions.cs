namespace InstrumentControlWorkbench.Cli;

public sealed record CliOptions(string Command, string Devices, string Plan, string? Archive, string? Csv, string? ControlFile, string? Sample, string? Ppms = null, string? Output = null, string? Routing = null)
{
    public const string Usage = "icw validate --devices FILE --plan FILE | icw run --devices FILE --plan FILE --archive DIR [--csv FILE] [--control-file FILE] [--sample NAME] | icw import --devices FILE --plan CONTEXT --ppms FILE --output FILE [--routing FILE]";
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("validate" or "run" or "import")) throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] is not ("--devices" or "--plan" or "--archive" or "--csv" or "--control-file" or "--sample" or "--ppms" or "--output" or "--routing") || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[index], args[index + 1])) throw new ArgumentException("Unknown, duplicate or incomplete option. " + Usage);
        }
        string Required(string key) => values.TryGetValue(key, out var value) ? Path.GetFullPath(value) : throw new ArgumentException(key + " is required.");
        string? OptionalPath(string key) => values.TryGetValue(key, out var value) ? Path.GetFullPath(value) : null;
        var archive = OptionalPath("--archive"); var csv = OptionalPath("--csv"); var control = OptionalPath("--control-file");
        if (args[0] == "run" && archive is null || args[0] == "validate" && values.Keys.Any(key => key is "--archive" or "--csv" or "--control-file" or "--sample" or "--ppms" or "--output" or "--routing")) throw new ArgumentException("Run requires --archive; validate accepts only --devices and --plan.");
        if (args[0] != "import" && values.Keys.Any(key => key is "--ppms" or "--output" or "--routing") || args[0] == "import" && values.Keys.Any(key => key is "--archive" or "--csv" or "--control-file" or "--sample")) throw new ArgumentException("Options do not apply to this command.");
        var devices = Required("--devices"); var plan = Required("--plan");
        var ppms = args[0] == "import" ? Required("--ppms") : null; var output = args[0] == "import" ? Required("--output") : null; var routing = OptionalPath("--routing");
        var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new[] { devices, plan, archive, csv, control, ppms, output, routing }.OfType<string>().ToArray();
        if (paths.Distinct(comparison).Count() != paths.Length) throw new ArgumentException("Input, archive, export and control paths must be distinct.");
        if (values.TryGetValue("--sample", out var sample) && sample.Length > 1024) throw new ArgumentException("Sample name exceeds 1024 characters.");
        return new(args[0], devices, plan, archive, csv, control, values.GetValueOrDefault("--sample"), ppms, output, routing);
    }
}

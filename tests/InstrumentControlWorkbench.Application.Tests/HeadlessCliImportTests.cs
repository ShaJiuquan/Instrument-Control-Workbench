using System.Text.Json;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Cli;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class HeadlessCliImportTests
{
    [Theory]
    [InlineData("ppms-field-voltage-import.json", 6)]
    [InlineData("ppms-field-samples-import.json", 28)]
    public async Task RoutingImportValidatesWithoutConnectionThenSavedPlanRunsReopensAndExports(string fixture, int rows)
    {
        using var files = new Files(fixture); var factory = new TrackingFactory(); var output = new StringWriter();
        var result = await new CommandLineRunner(factory).ExecuteAsync(files.Import(), output, CancellationToken.None); Assert.Equal(0, result); Assert.True(File.Exists(files.Output));
        Assert.False((await factory.Source!.GetSnapshotAsync(CancellationToken.None)).Connected);
        using (var record = JsonDocument.Parse(output.ToString())) { Assert.Equal("imported", record.RootElement.GetProperty("kind").GetString()); Assert.True(record.RootElement.GetProperty("routing").GetBoolean()); Assert.Equal(500, record.RootElement.GetProperty("maximum_steps").GetInt64()); }
        var native = JsonNode.Parse(await File.ReadAllTextAsync(files.Output))!; Assert.Equal(500, native["maximumStepExecutions"]!.GetValue<int>()); Assert.NotNull(native["routing"]);
        var runReports = new StringWriter(); result = await new CommandLineRunner().ExecuteAsync(CliOptions.Parse(["run", "--devices", files.Devices, "--plan", files.Output, "--archive", files.Archive, "--csv", files.Csv]), runReports, CancellationToken.None); Assert.Equal(0, result);
        using var report = JsonDocument.Parse(runReports.ToString().Trim().Split('\n')[^1]); Assert.Equal(rows, report.RootElement.GetProperty("rows").GetInt32()); Assert.False(report.RootElement.GetProperty("ownership_retained").GetBoolean());
        var id = new RunId(Guid.Parse(report.RootElement.GetProperty("run_id").GetString()![4..]));
        await using var archive = new FileRunArchive(files.Archive); var loaded = await archive.LoadAsync(id, CancellationToken.None); Assert.Equal(rows, loaded.DataSet.Rows.Count); Assert.True(loaded.IsFinalized); Assert.NotNull(loaded.DataSet.Plan!.Routing); Assert.True(File.Exists(files.Csv));
        if (rows == 28) { Assert.Contains(loaded.DataSet.Rows, row => row.Analysis is not null); Assert.Contains("sample", await File.ReadAllTextAsync(files.Csv), StringComparison.OrdinalIgnoreCase); }
    }

    [Fact]
    public async Task PureCurrentSourceAndStandaloneMeterImportWithoutRoutingAndRunWithExplicitRoles()
    {
        using var files = new Files("ppms-current-resistance-import.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "devices", "k6221-meter-devices.json"), files.Devices, true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "plans", "headless-current-import-context.plan.json"), files.Context, true);
        Assert.Equal(0, await new CommandLineRunner().ExecuteAsync(files.Import() with { Routing = null }, new StringWriter(), CancellationToken.None));
        var native = JsonNode.Parse(await File.ReadAllTextAsync(files.Output))!; Assert.Single(native["currentSourcePreparations"]!.AsArray()); Assert.Empty(native["preparations"]!.AsArray());
        var reports = new StringWriter(); Assert.Equal(0, await new CommandLineRunner().ExecuteAsync(CliOptions.Parse(["run", "--devices", files.Devices, "--plan", files.Output, "--archive", files.Archive]), reports, CancellationToken.None));
        using var result = JsonDocument.Parse(reports.ToString().Trim().Split('\n')[^1]); Assert.Equal(2, result.RootElement.GetProperty("rows").GetInt32()); Assert.False(result.RootElement.GetProperty("ownership_retained").GetBoolean());
    }
    [Fact]
    public async Task ContextBudgetIsEnforcedBeforeAnExistingOutputCanBeReplaced()
    {
        using var files = new Files("ppms-field-samples-import.json"); var factory = new TrackingFactory();
        var context = JsonNode.Parse(await File.ReadAllTextAsync(files.Context))!; context["maximumStepExecutions"] = 1; await File.WriteAllTextAsync(files.Context, context.ToJsonString()); await File.WriteAllTextAsync(files.Output, "original");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new CommandLineRunner(factory).ExecuteAsync(files.Import(), new StringWriter(), CancellationToken.None)); Assert.Equal("original", await File.ReadAllTextAsync(files.Output)); Assert.False((await factory.Source!.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task UnsupportedCommandAndMissingRoutingDoNotReplaceExistingOutputOrConnect()
    {
        using var files = new Files("ppms-field-voltage-import.json"); var factory = new TrackingFactory(); await File.WriteAllTextAsync(files.Output, "original");
        var noRouting = files.Import() with { Routing = null };
        await Assert.ThrowsAsync<FormatException>(() => new CommandLineRunner(factory).ExecuteAsync(noRouting, new StringWriter(), CancellationToken.None)); Assert.Equal("original", await File.ReadAllTextAsync(files.Output));
        await File.WriteAllTextAsync(files.Ppms, "{\"type\":\"command\",\"name\":\"not_migrated\",\"params\":{}}");
        await Assert.ThrowsAsync<FormatException>(() => new CommandLineRunner(factory).ExecuteAsync(files.Import(), new StringWriter(), CancellationToken.None)); Assert.Equal("original", await File.ReadAllTextAsync(files.Output)); Assert.False((await factory.Source!.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("version")]
    [InlineData("missing")]
    [InlineData("oversized")]
    public async Task InvalidRoutingSelectionRejectsBeforeSavingOrConnecting(string damage)
    {
        using var files = new Files("ppms-field-samples-import.json"); var factory = new TrackingFactory(); var selection = JsonNode.Parse(await File.ReadAllTextAsync(files.Routing))!;
        if (damage == "role") selection["wirings"]![0]!["source"] = "field";
        if (damage == "unknown") selection["silent_default"] = true;
        if (damage == "version") selection["version"] = 2;
        if (damage == "missing") selection["channels"] = "absent.json";
        var text = selection.ToJsonString(); if (damage == "duplicate") text = text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        if (damage == "oversized") text = new string(' ', 65537);
        await File.WriteAllTextAsync(files.Routing, text); await File.WriteAllTextAsync(files.Output, "previous");
        await Assert.ThrowsAnyAsync<Exception>(() => new CommandLineRunner(factory).ExecuteAsync(files.Import(), new StringWriter(), CancellationToken.None)); Assert.Equal("previous", await File.ReadAllTextAsync(files.Output)); Assert.False((await factory.Source!.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public async Task RoutingProfileCannotBeUsedAsOutputAndCancelledImportDoesNotWrite()
    {
        using var files = new Files("ppms-field-samples-import.json"); var profile = Path.Combine(Path.GetDirectoryName(files.Routing)!, "samples.json"); var original = await File.ReadAllTextAsync(profile);
        await Assert.ThrowsAsync<ArgumentException>(() => new CommandLineRunner().ExecuteAsync(files.Import() with { Output = profile }, new StringWriter(), CancellationToken.None)); Assert.Equal(original, await File.ReadAllTextAsync(profile));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CommandLineRunner().ExecuteAsync(files.Import(), new StringWriter(), cancellation.Token)); Assert.False(File.Exists(files.Output));
    }

    [Theory]
    [InlineData("import --devices d --plan p --output o")]
    [InlineData("import --devices d --plan p --ppms r")]
    [InlineData("import --devices d --plan p --ppms r --output o --archive a")]
    [InlineData("run --devices d --plan p --archive a --routing r")]
    [InlineData("import --devices d --plan p --ppms r --output r")]
    public void ImportOptionsRejectIncompleteCrossCommandAndInputOverwrite(string args) => Assert.Throws<ArgumentException>(() => CliOptions.Parse(args.Split(' ')));

    [Fact]
    public void OptionsKeepSampleAndImportFieldsInTheirOwnSlots()
    {
        var run = CliOptions.Parse(["run", "--devices", "d", "--plan", "p", "--archive", "a", "--sample", "Sample A"]); Assert.Equal("Sample A", run.Sample); Assert.Null(run.Ppms);
        var import = CliOptions.Parse(["import", "--devices", "d", "--plan", "p", "--ppms", "raw", "--output", "out", "--routing", "routing"]); Assert.Null(import.Sample); Assert.Equal(Path.GetFullPath("raw"), import.Ppms); Assert.Equal(Path.GetFullPath("out"), import.Output); Assert.Equal(Path.GetFullPath("routing"), import.Routing);
    }
    private sealed class TrackingFactory : IConfiguredDeviceFactory
    {
        private readonly ConfiguredDeviceFactory factory = new(); public MockKeithley2450SourceMeter? Source { get; private set; }
        public void Validate(DeviceConfiguration config) => factory.Validate(config);
        public ConfiguredDevicePorts Create(DeviceConfiguration config) { var ports = factory.Create(config); if (ports.SourceMeter is MockKeithley2450SourceMeter source) Source = source; return ports; }
    }
    private sealed class Files : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cli-import-" + Guid.NewGuid().ToString("N"));
        public string Devices => Path.Combine(root, "devices.json"); public string Context => Path.Combine(root, "context.json"); public string Ppms => Path.Combine(root, "raw.json"); public string Output => Path.Combine(root, "native.json"); public string Routing => Path.Combine(root, "profiles", "selection.json"); public string Archive => Path.Combine(root, "runs"); public string Csv => Path.Combine(root, "export.csv");
        public Files(string name)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Routing)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "devices", "headless-routing.devices.json"), Devices);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "plans", "headless-import-context.plan.json"), Context);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "plans", name), Ppms);
            foreach (var profile in new[] { "channel_profiles.json", "samples.json", "instrument_ports.json" }) File.Copy(Path.Combine(AppContext.BaseDirectory, "profiles", profile), Path.Combine(root, "profiles", profile));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "profiles", "headless-import-routing.json"), Routing);
        }
        public CliOptions Import() => CliOptions.Parse(["import", "--devices", Devices, "--plan", Context, "--ppms", Ppms, "--output", Output, "--routing", Routing]);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

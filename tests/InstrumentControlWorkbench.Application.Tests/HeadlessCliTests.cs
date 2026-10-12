using System.Text.Json;
using System.Threading.Channels;
using InstrumentControlWorkbench.Cli;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class HeadlessCliTests
{
    [Fact]
    public async Task ValidateIsMetadataOnlyAndRunPreservesFullArchiveAndCsvWithSimulationIdentity()
    {
        using var files = new Fixture(); var factory = new Factory(); var writer = new Reports();
        var code = await new CommandLineRunner(factory).ExecuteAsync(files.Options("validate"), writer, CancellationToken.None);
        Assert.Equal(0, code); Assert.False((await factory.Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False(Directory.Exists(files.Archive));
        code = await new CommandLineRunner(factory).ExecuteAsync(files.Options("run", csv: true), writer, CancellationToken.None);
        Assert.Equal(0, code); var result = await writer.Until("result"); var id = new RunId(Guid.Parse(result.GetProperty("run_id").GetString()![4..]));
        await using var archive = new FileRunArchive(files.Archive); var run = await archive.LoadAsync(id, CancellationToken.None);
        Assert.Equal(3, run.DataSet.Rows.Count); Assert.Equal("CLI sample", run.DataSet.Metadata!.SampleName); Assert.True(run.IsFinalized); Assert.Contains("Simulation", writer.Text); Assert.Contains("ownership_retained\":false", writer.Text); Assert.True(File.Exists(files.Csv));
        var snapshot = await factory.Source.GetSnapshotAsync(CancellationToken.None); Assert.False(snapshot.OutputEnabled); Assert.False(snapshot.Connected);
    }

    [Fact]
    public async Task CancellationFinalizesPartialRunWithOutputOffBeforeReturning130()
    {
        using var files = new Fixture(delay: true); var factory = new Factory(); var writer = new Reports(); using var cancellation = new CancellationTokenSource();
        var running = new CommandLineRunner(factory).ExecuteAsync(files.Options("run"), writer, cancellation.Token);
        var started = await writer.Until("started"); cancellation.Cancel(); Assert.Equal(130, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        var snapshot = await factory.Source.GetSnapshotAsync(CancellationToken.None); Assert.False(snapshot.OutputEnabled); Assert.False(snapshot.Connected);
        await using var archive = new FileRunArchive(files.Archive); var run = await archive.LoadAsync(new(Guid.Parse(started.GetProperty("run_id").GetString()![4..])), CancellationToken.None); Assert.Equal(MeasurementRunState.Aborted, run.DataSet.State); Assert.True(run.IsFinalized);
    }

    [Fact]
    public async Task ControlFileTargetsCurrentRunRejectsStaleRequestsAndPausesResumesAborts()
    {
        using var files = new Fixture(delay: true); var factory = new Factory(); var writer = new Reports();
        var running = new CommandLineRunner(factory).ExecuteAsync(files.Options("run", control: true), writer, CancellationToken.None);
        var started = await writer.Until("started"); var id = started.GetProperty("run_id").GetString()!;
        await files.Command(Guid.NewGuid().ToString("N"), "abort"); await writer.Until("control_rejected"); Assert.False(running.IsCompleted);
        await files.Command(id, "pause"); await writer.Until("control_applied");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await factory.Source.GetSnapshotAsync(deadline.Token)).OutputEnabled) await Task.Delay(10, deadline.Token);
        while (true)
        {
            await files.Command(id, "status"); var status = await writer.Until("control_applied");
            if (status.GetProperty("phase").GetString() == "Paused") break;
            deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(10, deadline.Token);
        }
        await files.Command(id, "resume"); await writer.Until("control_applied"); await files.Command(id, "abort"); await writer.Until("control_applied");
        Assert.Equal(130, await running.WaitAsync(TimeSpan.FromSeconds(5))); Assert.False((await factory.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }

    [Fact]
    public async Task FailedStopKeepsRunnerAliveUntilExplicitRecoveryThenReportsFaultedArchive()
    {
        using var files = new Fixture(); var factory = new Factory(); factory.Source.StopFails = true; var writer = new Reports();
        var running = new CommandLineRunner(factory, TimeSpan.FromSeconds(1)).ExecuteAsync(files.Options("run", control: true), writer, CancellationToken.None);
        var safety = await writer.Until("safety_recovery_required"); Assert.False(running.IsCompleted); Assert.True(safety.GetProperty("ownership_retained").GetBoolean());
        var id = safety.GetProperty("run_id").GetString()!;
        await files.Command(id, "recover"); await writer.Until("control_rejected"); Assert.False(running.IsCompleted);
        factory.Source.StopFails = false; await files.Command(id, "recover"); await writer.Until("control_applied"); Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False((await factory.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); await using var archive = new FileRunArchive(files.Archive); var result = await archive.LoadAsync(new(Guid.Parse(id[4..])), CancellationToken.None); Assert.Equal(MeasurementRunState.Faulted, result.DataSet.State); Assert.Equal(3, result.DataSet.Rows.Count);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    public async Task MalformedControlDoesNotStopTheOwnedRunAndNextValidAbortStillWorks(string damage)
    {
        using var files = new Fixture(delay: true); var factory = new Factory(); var writer = new Reports();
        var running = new CommandLineRunner(factory).ExecuteAsync(files.Options("run", control: true), writer, CancellationToken.None); var id = (await writer.Until("started")).GetProperty("run_id").GetString()!;
        var request = Guid.NewGuid(); var prefix = $"\"version\":1,\"request_id\":\"{request}\",\"run_id\":\"{id}\",\"command\":\"abort\"";
        var json = damage == "unknown" ? "{" + prefix + ",\"extra\":1}" : damage == "duplicate" ? "{" + prefix + ",\"command\":\"status\"}" : "{" + prefix.Replace("\"version\":1", "\"version\":2") + "}";
        await File.WriteAllTextAsync(files.Control, json); await writer.Until("control_rejected"); Assert.False(running.IsCompleted);
        await files.Command(id, "abort"); Assert.Equal(130, await running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReusingAnActuationIdCannotResumeAPausedRun()
    {
        using var files = new Fixture(delay: true); var factory = new Factory(); var writer = new Reports();
        var running = new CommandLineRunner(factory).ExecuteAsync(files.Options("run", control: true), writer, CancellationToken.None);
        var id = (await writer.Until("started")).GetProperty("run_id").GetString()!; var request = Guid.NewGuid();
        await File.WriteAllTextAsync(files.Control, JsonSerializer.Serialize(new { version = 1, request_id = request, run_id = id, command = "pause" })); await writer.Until("control_applied");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await files.Command(id, "status"); var status = await writer.Until("control_applied");
            if (status.GetProperty("phase").GetString() == "Paused") break;
            await Task.Delay(10, deadline.Token);
        }
        await File.WriteAllTextAsync(files.Control, JsonSerializer.Serialize(new { version = 1, request_id = request, run_id = id, command = "resume" })); await writer.Until("control_rejected");
        Assert.False((await factory.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(running.IsCompleted);
        await files.Command(id, "abort"); Assert.Equal(130, await running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReportingFailureAfterStartStillAbortsFinalizesAndDisconnectsBeforeReturning()
    {
        using var files = new Fixture(delay: true); var factory = new Factory();
        var code = await new CommandLineRunner(factory).ExecuteAsync(files.Options("run"), new BrokenReports(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, code); var snapshot = await factory.Source.GetSnapshotAsync(CancellationToken.None); Assert.False(snapshot.OutputEnabled); Assert.False(snapshot.Connected);
        await using var archive = new FileRunArchive(files.Archive); var catalog = await archive.ListAsync(CancellationToken.None); var result = Assert.Single(catalog.Runs); Assert.True(result.IsFinalized); Assert.Equal(MeasurementRunState.Aborted, result.Header.State);
    }
    private sealed class BrokenReports : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value!.Contains("\"kind\":\"started\"", StringComparison.Ordinal) ? Task.FromException(new IOException("output closed")) : base.WriteLineAsync(value);
    }

    [Fact]
    public async Task InvalidPlanAndMissingDevicesNeverStartArchiveOrConnect()
    {
        using var files = new Fixture(); var factory = new Factory(); await File.WriteAllTextAsync(files.Plan, "invalid");
        await Assert.ThrowsAnyAsync<Exception>(() => new CommandLineRunner(factory).ExecuteAsync(files.Options("run"), new StringWriter(), CancellationToken.None)); Assert.False(Directory.Exists(files.Archive)); Assert.False((await factory.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        File.Delete(files.Devices); await Assert.ThrowsAsync<FileNotFoundException>(() => new CommandLineRunner(factory).ExecuteAsync(files.Options("validate"), new StringWriter(), CancellationToken.None));
    }

    [Theory]
    [InlineData("run --devices dev --plan plan")]
    [InlineData("validate --devices dev --plan plan --archive runs")]
    [InlineData("validate --devices dev --plan plan --plan other")]
    [InlineData("run --devices dev --plan plan --archive runs --unknown x")]
    [InlineData("validate --devices same --plan same")]
    public void OptionsRejectMissingDuplicateUnknownAndConflictingPaths(string args) => Assert.Throws<ArgumentException>(() => CliOptions.Parse(args.Split(' ')));

    private sealed class Reports : StringWriter
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>(); private readonly object gate = new();
        public string Text { get { lock (gate) return ToString(); } }
        public override Task WriteLineAsync(string? value) { lock (gate) WriteLine(value); lines.Writer.TryWrite(value!); return Task.CompletedTask; }
        public async Task<JsonElement> Until(string kind)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (true) { using var json = JsonDocument.Parse(await lines.Reader.ReadAsync(deadline.Token)); if (json.RootElement.GetProperty("kind").GetString() == kind) return json.RootElement.Clone(); }
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "headless-cli-" + Guid.NewGuid().ToString("N"));
        public string Devices => Path.Combine(root, "devices.json"); public string Plan => Path.Combine(root, "plan.json"); public string Archive => Path.Combine(root, "runs"); public string Csv => Path.Combine(root, "export.csv"); public string Control => Path.Combine(root, "control.json");
        public Fixture(bool delay = false)
        {
            Directory.CreateDirectory(root);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "devices", "headless-simulation.devices.json"), Devices);
            var source = new InstrumentId("k2450"); var preparation = new SourcePreparation(source, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true));
            var read = new ReadSourceSenseStep(new("read"), source, SourceFunction.Voltage, new("V"), new("I"));
            var body = new ForEachStep(new("loop"), new("iteration"), new ScanValues([new(1, PhysicalUnit.Dimensionless), new(2, PhysicalUnit.Dimensionless), new(3, PhysicalUnit.Dimensionless)]), new SequenceStep(new("point"), [read, new DelayStep(new("delay"), delay ? TimeSpan.FromSeconds(30) : TimeSpan.Zero)]));
            var plan = new ExperimentPlan("Headless fixture", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), source, true), body]), [preparation]); File.WriteAllText(Plan, new JsonExperimentPlanDocument().Serialize(plan));
        }
        public CliOptions Options(string command, bool csv = false, bool control = false) => new(command, Devices, Plan, command == "run" ? Archive : null, csv ? Csv : null, control ? Control : null, command == "run" ? "CLI sample" : null);
        public Task Command(string id, string command) => File.WriteAllTextAsync(Control, JsonSerializer.Serialize(new { version = 1, request_id = Guid.NewGuid(), run_id = id, command }));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Factory : IConfiguredDeviceFactory
    {
        private readonly ConfiguredDeviceFactory inner = new(); public SourceDriver Source { get; } = new();
        public void Validate(DeviceConfiguration configuration) => inner.Validate(configuration);
        public ConfiguredDevicePorts Create(DeviceConfiguration configuration) => configuration.Category == ConfiguredDeviceCategory.SourceMeter ? new(Source) : inner.Create(configuration);
    }
    private sealed class SourceDriver : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new(new("k2450"), "Headless source simulator"); public volatile bool StopFails;
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token); public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token); public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token); public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint source, CancellationToken token) => inner.ConfigureSourceAsync(source, token); public Task ConfigureSenseAsync(SenseConfiguration sense, CancellationToken token) => inner.ConfigureSenseAsync(sense, token); public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token); public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token); public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token) => inner.ReadSourceSenseAsync(token);
        public Task SafeStopAsync(CancellationToken token) => StopFails ? Task.FromException(new IOException("stop failed")) : inner.SafeStopAsync(token);
    }
}

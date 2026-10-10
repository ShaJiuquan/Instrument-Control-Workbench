using System.Text;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class RoutingProfileWorkspaceTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "profiles", name);
    private static RoutingProfileWorkspace Workspace() => new(new JsonRoutingProfileDocument(), new PpmsRoutingProfileImporter());
    private static Task<ValidatedRoutingProfileDocument> Import(RoutingProfileWorkspace workspace) => workspace.ImportAsync(Fixture("channel_profiles.json"), Fixture("samples.json"), Fixture("instrument_ports.json"), Token);

    [Fact]
    public async Task NativeDocumentRoundTripsEveryAuthoredFieldAndUnboundPhysicalFamily()
    {
        var workspace = Workspace(); var original = await Import(workspace); var restored = workspace.Validate(original.Text);
        Assert.Equal(original.Text, restored.Text); Assert.Equal(3, restored.Catalog.Wirings.Count); Assert.Equal(4, restored.Catalog.Samples.Count);
        Assert.Equal("k6221_k2182", restored.Catalog.ModeWirings["curr_delta"].Value);
        Assert.Equal(original.Catalog.Channels[0].Connections.ToArray(), restored.Catalog.Channels[0].Connections.ToArray());
        Assert.Equal(original.Catalog.Samples[0].Electrodes.ToArray(), restored.Catalog.Samples[0].Electrodes.ToArray());
        Assert.Equal(original.Catalog.Samples[0].Geometry, restored.Catalog.Samples[0].Geometry);
        Assert.Equal(original.Text, (await workspace.LoadAsync(Fixture("workbench-routing.json"), Token)).Text);
    }
    [Fact]
    public async Task AuthoredSharedPortsPersistButCannotBypassFourWirePreflight()
    {
        var w = Workspace(); var catalog = (await Import(w)).Catalog; var layout = catalog.Wirings[0];
        var shared = w.UpdateWiring(catalog, new(layout.Id, layout.Label, layout.SourcePlus, layout.SourceMinus, layout.SourcePlus, layout.SourceMinus));
        var restored = w.Validate(shared.Text); var b = new Bench(); var binding = b.Bind(restored.Catalog);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => b.Plans.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-samples-import.json"), binding, Token));
        Assert.Contains("Four-wire measurement requires four independent", error.Message);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task TypedEditsFreezeSourceAndPreserveMetadataThenPermitExplicitDeletion()
    {
        var w = Workspace(); var catalog = (await Import(w)).Catalog; var channel = catalog.Channels[0]; var sample = catalog.Samples[0]; var layout = catalog.Wirings[0];
        var newChannel = new ChannelProfile(channel.Id, "Edited contacts", channel.Connections, channel.OriginalId, channel.GroupIndex, channel.Alias, "new notes");
        var changed = w.UpdateChannel(catalog, newChannel).Catalog; Assert.Equal("CH1", catalog.Channels[0].Name); Assert.Equal("Edited contacts", changed.Channels[0].Name);
        var newSample = new SampleProfile(sample.Id, "Edited specimen", sample.Topology, sample.Electrodes, sample.Geometry, sample.OriginalId, sample.Material, sample.Substrate, "edited");
        changed = w.UpdateSample(changed, newSample).Catalog;
        changed = w.UpdateWiring(changed, new(layout.Id, "Edited wiring", layout.SourcePlus, layout.SourceMinus, layout.SensePlus, layout.SenseMinus)).Catalog;
        Assert.Equal("Edited specimen", changed.Samples[0].Name); Assert.Equal("Edited wiring", changed.Wirings[0].Label);
        var extraChannel = new ChannelProfile(new("extra"), "Unused", [new(new("spare"), null, null, false)]);
        changed = w.UpdateChannel(changed, extraChannel).Catalog; changed = w.DeleteChannel(changed, extraChannel.Id).Catalog;
        changed = w.SelectActive(changed, changed.Channels[1].Id, changed.Samples[1].Id).Catalog;
        changed = w.DeleteSample(changed, sample.Id).Catalog;
        var unused = new MatrixWiringLayout(new("extra"), "Unbound", new(1, 5), new(1, 6), new(1, 7), new(1, 8));
        changed = w.UpdateWiring(changed, unused).Catalog; changed = w.SetModeWiring(changed, "test_mode", unused.Id).Catalog;
        Assert.Throws<ArgumentException>(() => w.DeleteWiring(changed, unused.Id));
        changed = w.SetModeWiring(changed, "test_mode", null).Catalog; changed = w.DeleteWiring(changed, unused.Id).Catalog;
        Assert.DoesNotContain(changed.Wirings, wire => wire.Id == unused.Id); Assert.Equal(4, catalog.Samples.Count);
    }
    [Theory]
    [InlineData("active")]
    [InlineData("channel")]
    [InlineData("connection")]
    [InlineData("wiring")]
    [InlineData("mode")]
    [InlineData("unknown")]
    public async Task BrokenEditsRejectWithoutChangingOriginalCatalog(string damage)
    {
        var w = Workspace(); var original = await Import(w); var c = original.Catalog;
        Action action = damage switch
        {
            "active" => () => w.SelectActive(c, new("missing"), c.ActiveSample),
            "channel" => () => w.DeleteChannel(c, c.Channels[1].Id),
            "connection" => () => w.UpdateChannel(c, new(c.Channels[0].Id, "Broken", c.Channels[0].Connections.Skip(1))),
            "wiring" => () => w.DeleteWiring(c, c.Wirings[0].Id),
            "mode" => () => w.SetModeWiring(c, "volt_dc", new("missing")),
            _ => () => w.DeleteSample(c, new("missing")),
        };
        Assert.NotNull(Record.Exception(action)); Assert.Equal(original.Text, w.Inspect(c).Text);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("format")]
    [InlineData("link")]
    [InlineData("geometry")]
    [InlineData("active")]
    public async Task InvalidNativeSavePreservesPreviousFileAndCreatesNoTemporaryDebris(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "routing-doc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = Workspace(); var original = await Import(w); var path = Path.Combine(folder, "profiles.json"); await w.SaveAsync(path, original.Text, Token);
            var root = JsonNode.Parse(original.Text)!;
            switch (damage)
            {
                case "unknown": root["wirings"]![0]!["sourceMeter"] = "guessed"; break;
                case "version": root["version"] = 2; break;
                case "format": root["format"] = "instrument-workbench-plan"; break;
                case "link": root["samples"]![0]!["electrodes"]![0]!["connection"] = "missing"; break;
                case "geometry": root["samples"]![0]!["geometry"]!["width"]!["value"] = -1; break;
                case "active": root["activeChannel"] = "missing"; break;
            }
            var text = root.ToJsonString(); if (damage == "duplicate") text = text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
            Assert.NotNull(await Record.ExceptionAsync(() => w.SaveAsync(path, text, Token))); Assert.Equal(original.Text, await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(folder, ".*.tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task NativeFileAcceptsBomRejectsInvalidUtf8AndHonorsCancellationAndRenameFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "routing-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = Workspace(); var document = await Import(w); var path = Path.Combine(folder, "profiles.json"); await w.SaveAsync(path, document.Text, Token);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.LoadAsync("missing", cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.SaveAsync(path, document.Text, cancelled.Token)); Assert.Equal(document.Text, await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, "\uFEFF" + document.Text, new UTF8Encoding(false)); Assert.Equal(document.Text, (await w.LoadAsync(path, Token)).Text);
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]); await Assert.ThrowsAsync<DecoderFallbackException>(() => w.LoadAsync(path, Token));
            var directory = Path.Combine(folder, "directory"); Directory.CreateDirectory(directory);
            Assert.NotNull(await Record.ExceptionAsync(() => w.SaveAsync(directory, document.Text, Token))); Assert.Empty(Directory.GetFiles(folder, ".*.tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("bytes")]
    [InlineData("depth")]
    [InlineData("values")]
    public void ParseRejectsDocumentBudgets(string budget)
    {
        var text = budget switch
        {
            "bytes" => new string(' ', JsonRoutingProfileDocument.MaximumBytes + 1),
            "depth" => new string('[', 33) + "0" + new string(']', 33),
            _ => "{\"extra\":[" + string.Join(',', Enumerable.Repeat("0", 100_001)) + "]}",
        };
        Assert.Throws<FormatException>(() => new JsonRoutingProfileDocument().Parse(text));
    }
    [Fact]
    public async Task SavedCatalogBindsAfterOriginalFilesDisappearAndRunsFrozenPlanIntoFreshArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "routing-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var paths = new[] { "channel_profiles.json", "samples.json", "instrument_ports.json" }.Select(name => Path.Combine(folder, name)).ToArray();
            for (var i = 0; i < paths.Length; i++) File.Copy(Fixture(Path.GetFileName(paths[i])), paths[i]);
            var w = Workspace(); var imported = await w.ImportAsync(paths[0], paths[1], paths[2], Token); var native = Path.Combine(folder, "routing.json"); await w.SaveAsync(native, imported.Text, Token);
            foreach (var path in paths) File.Delete(path);
            var catalog = (await Workspace().LoadAsync(native, Token)).Catalog; RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var binding = b.Bind(catalog);
                var plan = await b.Plans.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-samples-import.json"), binding, Token);
                var oldName = catalog.Samples[0].Name; var sample = catalog.Samples[0];
                var changed = w.UpdateSample(catalog, new(sample.Id, "Next-run name", sample.Topology, sample.Electrodes, sample.Geometry, sample.OriginalId, sample.Material, sample.Substrate, sample.Notes));
                Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
                using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(plan.Plan, Token);
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.DataRowReceived) { }
                    b.Runs.Pause(); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
                    Assert.True(b.Runs.HasActiveRun); await w.SaveAsync(native, changed.Text, Token); Assert.Equal(oldName, plan.Plan.Routing!.Samples[0].Name);
                    b.Runs.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally { if (!running.IsCompleted) { b.Runs.Abort(); await running; } }
                id = b.Runs.LatestDataSet!.Id; Assert.Equal(28, b.Runs.LatestDataSet.Rows.Count);
                Assert.All(b.Runs.LatestDataSet.Rows.Where(r => r.Routing?.Binding.Sample == sample.Id), row => Assert.Equal(oldName, row.Routing!.SampleName));
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
            }
            await using var reader = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reader.LoadAsync(id, Token); Assert.Equal(28, stored.DataSet.Rows.Count);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(28, fresh.Runs.LatestDataSet!.Rows.Count);
            Assert.Equal("Next-run name", (await Workspace().LoadAsync(native, Token)).Catalog.Samples[0].Name);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new(); public MockMatrixSwitch Matrix { get; }
        public ExperimentImportBindings Devices { get; }
        public ExperimentPlanWorkspace Plans { get; }
        public MeasurementRunService Runs { get; }
        public Bench(FileRunArchive? archive = null)
        {
            var hub = Hub; var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var manager = new InstrumentManager(Source, hub); var field = MockEnvironmentCatalog.Field();
            var matrixId = new InstrumentId("matrix"); Matrix = new(new(new(matrixId, "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 8, 10)], [Source.Id]));
            var registry = new DeviceRegistry([manager, new MatrixSwitchSession(Matrix, hub), new AxisControllerSession(field, hub)], leases);
            Runs = new(manager, leases, hub, archive is null ? null : [archive], deviceRegistry: registry); Plans = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
            Devices = new([KeyValuePair.Create("k2450", Source.Id), KeyValuePair.Create("k3706", matrixId)], [KeyValuePair.Create("field", field.Definition)],
                [new(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        }
        public ExperimentImportBindings Bind(RoutingProfileCatalog catalog) => catalog.Bind(Devices, Matrix.Definition.Device.Id, [KeyValuePair.Create(new WiringProfileId("k2450"), Source.Id)], new("k2450"));
        public NotificationHub Hub { get; } = new();
    }
}

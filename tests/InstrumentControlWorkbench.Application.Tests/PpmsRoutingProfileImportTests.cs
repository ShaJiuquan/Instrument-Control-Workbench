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
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PpmsRoutingProfileImportTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "profiles", name);
    private static (JsonObject Channels, JsonObject Samples, JsonObject Ports) Documents() =>
        ((JsonObject)JsonNode.Parse(File.ReadAllText(Fixture("channel_profiles.json")))!,
         (JsonObject)JsonNode.Parse(File.ReadAllText(Fixture("samples.json")))!,
         (JsonObject)JsonNode.Parse(File.ReadAllText(Fixture("instrument_ports.json")))!);
    private static RoutingProfileCatalog Parse((JsonObject Channels, JsonObject Samples, JsonObject Ports) d) =>
        new PpmsRoutingProfileImporter().Parse(d.Channels.ToJsonString(), d.Samples.ToJsonString(), d.Ports.ToJsonString());
    private static string Measurement(string query, bool sample = false, bool fourWire = true) => new JsonObject
    {
        ["type"] = "measurement",
        ["name"] = sample ? "sample_r" : "resistance",
        ["params"] = new JsonObject { [sample ? "sample_id" : "channel"] = query, ["mode"] = "volt_dc", ["sensing_mode"] = fourWire, ["settle_s"] = 0 },
    }.ToJsonString();

    [Fact]
    public async Task WorkspaceLoadsAllThreeSchemasAndPreservesAuthoredMetadataAndUnboundFamilies()
    {
        var b = new Bench(); var catalog = await b.LoadAsync();
        Assert.Equal(2, catalog.Channels.Count); Assert.Equal(4, catalog.Samples.Count); Assert.Equal(3, catalog.Wirings.Count);
        Assert.Equal("k6221_k2182", catalog.ModeWirings["curr_dc"].Value);
        var channel = catalog.Channels[0]; Assert.Equal("Legacy Channel A", channel.OriginalId); Assert.Equal("main contacts", channel.Alias);
        Assert.Equal(1, channel.GroupIndex); Assert.Equal("Simulation fixture only", channel.Notes);
        var connection = channel.Connections[0]; Assert.Equal(8, connection.DisplayRow); Assert.Equal("display-only-1", connection.Raw);
        Assert.Equal(1, connection.Index); Assert.Equal("i_plus", connection.Label); Assert.Equal("Fixture connection i_plus", connection.Notes);
        var unassigned = channel.Connections[^1]; Assert.False(unassigned.Enabled); Assert.Null(unassigned.Column);
        var sample = catalog.Samples[0]; Assert.Equal("Legacy specimen 1", sample.OriginalId); Assert.Equal("Calibration film", sample.Material);
        Assert.Equal("Simulation substrate", sample.Substrate); Assert.Equal(0.0015, sample.Geometry.Spacing!.Value.Value);
        var bindings = b.Bind(catalog); Assert.Single(bindings.Samples!.Routing.Wirings); Assert.Single(bindings.Samples.ModeWirings);
        Assert.Equal("Simulated independent force/sense ports", bindings.Samples.Routing.Wirings[0].Label);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("channel1")]
    [InlineData("CH1")]
    [InlineData("1")]
    [InlineData("Legacy Channel A")]
    [InlineData("main contacts")]
    [InlineData("__active__")]
    public async Task ChannelAliasesUseInstrumentRowsAndPreserveCatalogInNativePlan(string query)
    {
        var b = new Bench(); var catalog = await b.LoadAsync();
        var document = b.Workspace.Import(Measurement(query), b.Bind(catalog));
        Assert.NotNull(document.Plan.Routing); Assert.Contains("display-only-1", document.Text);
        var route = Assert.Single(Assert.IsType<SequenceStep>(document.Plan.Root).Children.OfType<RouteMatrixStep>());
        Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 1), new(1, 2, 2), new(1, 3, 3), new(1, 4, 4) }, route.Closed);
        await b.Runs.StartPlanAsync(document.Plan, Token); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Theory]
    [InlineData("sample1")]
    [InlineData("1")]
    [InlineData("Specimen 1")]
    [InlineData("Legacy specimen 1")]
    public async Task ImportedSampleKeysIdsNumbersAndNamesExecuteWithoutManualAliases(string query)
    {
        var b = new Bench(); var document = b.Workspace.Import(Measurement(query, sample: true), b.Bind(await b.LoadAsync()));
        await b.Runs.StartPlanAsync(document.Plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal("sample1", row.Routing!.Binding.Sample.Value);
        Assert.Equal(4, row.Routing.Closed.Count);
    }
    [Fact]
    public async Task CrossChannelHallElectrodesUseLinkedColumnsAndRetainCompleteAnalysis()
    {
        var b = new Bench(); var document = b.Workspace.Import(Measurement("sample2", sample: true), b.Bind(await b.LoadAsync()));
        await b.Runs.StartPlanAsync(document.Plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(3, rows.Count); Assert.NotNull(rows[2].Analysis);
        Assert.Equal(new[] { 1, 2, 9, 10 }, rows[1].Routing!.Roles.Select(r => r.Point.Column));
        Assert.Equal("channel2", rows[1].Routing!.Roles[2].Channel.Value);
    }
    [Fact]
    public async Task SharedTwoWirePortsRemainSharedAndAreNotInventedAsFourWire()
    {
        var d = Documents(); ((JsonObject)d.Ports["mode_wiring"]!)["volt_dc"] = "k2450_two_wire";
        var b = new Bench(twoWire: true); var bindings = b.Bind(Parse(d), "k2450_two_wire");
        var document = b.Workspace.Import(Measurement("sample4", sample: true, fourWire: false), bindings);
        await b.Runs.StartPlanAsync(document.Plan, Token); Assert.Equal(2, Assert.Single(b.Runs.LatestDataSet!.Rows).Routing!.Closed.Count);
        Assert.Throws<FormatException>(() => b.Workspace.Import(Measurement("sample1", sample: true), bindings));
    }
    [Fact]
    public async Task LoadedProfilesFreezeBeforeAuthorEditsAndNativeArchiveKeepsMetadata()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ppms-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive: archive); var d = Documents(); var catalog = Parse(d); var bindings = b.Bind(catalog);
                ((JsonObject)d.Samples["samples"]!["sample1"]!)["name"] = "Edited after import";
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-samples-import.json"), bindings, Token);
                var native = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(native, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(native, Token)).Plan, Token);
                Assert.Equal(28, b.Runs.LatestDataSet!.Rows.Count); id = b.Runs.LatestDataSet.Id;
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(id, Token);
            var config = stored.DataSet.Plan!.Routing!; var sample = config.Samples[0];
            Assert.Equal("Specimen 1", sample.Name); Assert.Equal("Calibration film", sample.Material); Assert.Equal("Legacy specimen 1", sample.OriginalId);
            Assert.Equal(0.0015, sample.Geometry.Spacing!.Value.Value); Assert.Equal("Contact I+", sample.Electrodes[0].Notes);
            Assert.Equal("main contacts", config.Channels[0].Alias); Assert.Equal("display-only-1", config.Channels[0].Connections[0].Raw);
            Assert.Equal("Simulated independent force/sense ports", config.Wirings[0].Label);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan, Token); Assert.Equal(28, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "result.csv"); await reopened.ExportCsvAsync(id, csv, Token); Assert.Contains("routing_json", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("active-channel")]
    [InlineData("active-sample")]
    [InlineData("duplicate-channel")]
    [InlineData("duplicate-sample")]
    [InlineData("missing-channel-link")]
    [InlineData("missing-connection-link")]
    [InlineData("missing-wiring")]
    [InlineData("topology")]
    [InlineData("geometry-zero")]
    [InlineData("geometry-string")]
    [InlineData("unknown-field")]
    [InlineData("negative-column")]
    [InlineData("null-port-slot")]
    [InlineData("missing-port-row")]
    [InlineData("invalid-bool")]
    [InlineData("missing-label")]
    [InlineData("long-text")]
    public void BrokenConfigurationCannotBecomeAnExecutableCatalog(string damage)
    {
        var d = Documents(); var channel = (JsonObject)d.Channels["profiles"]!["channel1"]!;
        var sample = (JsonObject)d.Samples["samples"]!["sample1"]!; var electrode = (JsonObject)sample["electrodes"]![0]!;
        var connection = (JsonObject)channel["connections"]![0]!; var port = (JsonObject)d.Ports["wiring"]!["k2450"]!["source_plus"]!;
        switch (damage)
        {
            case "active-channel": d.Channels["active"] = "missing"; break;
            case "active-sample": d.Samples["active"] = "missing"; break;
            case "duplicate-channel": ((JsonObject)d.Channels["profiles"]!)["CHANNEL1"] = channel.DeepClone(); break;
            case "duplicate-sample": ((JsonObject)d.Samples["samples"]!)["SAMPLE1"] = sample.DeepClone(); break;
            case "missing-channel-link": electrode["link"]!["channel"] = "missing"; break;
            case "missing-connection-link": electrode["link"]!["connection"] = "missing"; break;
            case "missing-wiring": d.Ports["mode_wiring"]!["curr_dc"] = "missing"; break;
            case "topology": sample["topology"] = "unknown"; break;
            case "geometry-zero": sample["geometry"]!["spacing_m"] = 0; break;
            case "geometry-string": sample["geometry"]!["width_m"] = "NaN"; break;
            case "unknown-field": connection["unsafe_relay"] = true; break;
            case "negative-column": connection["route"]!["col"] = -1; break;
            case "null-port-slot": port["slot"] = null; break;
            case "missing-port-row": port.Remove("row"); break;
            case "invalid-bool": connection["enabled"] = "false"; break;
            case "missing-label": ((JsonArray)sample["electrodes"]!).RemoveAt(0); break;
            case "long-text": sample["notes"] = new string('x', 8193); break;
        }
        Assert.Throws<FormatException>(() => Parse(d));
    }
    [Theory]
    [InlineData("disabled")]
    [InlineData("slot")]
    [InlineData("opposite-poles")]
    [InlineData("ambiguous-alias")]
    [InlineData("opposite-ports")]
    [InlineData("shared-force-sense")]
    [InlineData("shared-force-sense-terminals")]
    [InlineData("mode-wiring")]
    public async Task UnusableSelectedChannelRejectsDuringPreflightBeforeConnection(string damage)
    {
        var d = Documents(); var c = d.Channels["profiles"]!["channel1"]!;
        switch (damage)
        {
            case "disabled": c["connections"]![0]!["enabled"] = false; break;
            case "slot": c["connections"]![0]!["route"]!["slot"] = 2; break;
            case "opposite-poles": c["connections"]![1]!["route"]!["col"] = 1; break;
            case "opposite-ports": d.Ports["wiring"]!["k2450"]!["source_minus"]!["row"] = 1; break;
            case "shared-force-sense": d.Ports["wiring"]!["k2450"]!["sense_plus"]!["row"] = 1; break;
            case "shared-force-sense-terminals": c["connections"]![2]!["route"]!["col"] = 1; break;
            case "mode-wiring": d.Ports["mode_wiring"]!["volt_dc"] = "k2450_two_wire"; break;
            case "ambiguous-alias": d.Channels["profiles"]!["channel2"]!["alias"] = "main contacts"; break;
        }
        var b = new Bench(); var bindings = b.Bind(Parse(d)); Assert.Throws<FormatException>(() => b.Workspace.Import(Measurement("main contacts"), bindings));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public void DuplicatePropertiesDepthValuesAndByteBudgetsRejectBeforeMaterialization()
    {
        var d = Documents(); var importer = new PpmsRoutingProfileImporter();
        var error = Assert.Throws<FormatException>(() => importer.Parse("{\"active\":\"channel1\",\"active\":\"channel2\"}", d.Samples.ToJsonString(), d.Ports.ToJsonString()));
        Assert.Contains("channel_profiles.json", error.Message); Assert.Contains("Duplicate", error.Message);
        Assert.Throws<FormatException>(() => importer.Parse(new string(' ', PpmsRoutingProfileImporter.MaximumBytes + 1), "{}", "{}"));
        Assert.Throws<FormatException>(() => importer.Parse(new string('[', 33) + "0" + new string(']', 33), "{}", "{}"));
        Assert.Throws<FormatException>(() => importer.Parse("[" + string.Join(',', Enumerable.Repeat("0", 10001)) + "]", "{}", "{}"));
    }
    [Fact]
    public async Task AsyncLoaderSupportsBomCancellationAndStrictUtf8WithoutTouchingSources()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ppms-profile-load-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var d = Documents(); var paths = new[] { "channels", "samples", "ports" }.Select(n => Path.Combine(folder, n + ".json")).ToArray();
            foreach (var pair in paths.Zip(new[] { d.Channels, d.Samples, d.Ports })) await File.WriteAllTextAsync(pair.First, pair.Second.ToJsonString(), new UTF8Encoding(true));
            var b = new Bench(); var catalog = await b.Workspace.LoadRoutingProfilesAsync(paths[0], paths[1], paths[2], Token); Assert.Equal(4, catalog.Samples.Count);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Workspace.LoadRoutingProfilesAsync("missing", "missing", "missing", cancelled.Token));
            await File.WriteAllBytesAsync(paths[0], [0xFF, 0xFE, 0xFF]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => b.Workspace.LoadRoutingProfilesAsync(paths[0], paths[1], paths[2], Token));
            Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void SixtyFourChannelsDoNotConsumeBindingBudgetWithExpandedAliasCopies()
    {
        var d = Documents(); var original = d.Channels["profiles"]!["channel1"]!; var profiles = new JsonObject();
        for (var i = 1; i <= 64; i++)
        {
            var profile = (JsonObject)original.DeepClone(); profile["channel_id"] = "Channel" + i; profile["name"] = "CH" + i; profile["alias"] = "Alias " + i;
            profiles["channel" + i] = profile;
        }
        d.Channels["profiles"] = profiles;
        var catalog = Parse(d); var b = new Bench(); var bindings = b.Bind(catalog);
        Assert.Empty(bindings.Channels); Assert.Equal("channel64", catalog.ResolveChannel("Alias 64").Id.Value);
        Assert.NotNull(b.Workspace.Import(Measurement("CH64"), bindings));
        profiles["channel65"] = original.DeepClone(); Assert.Throws<FormatException>(() => Parse(d));
    }
    [Theory]
    [InlineData("missing-wiring")]
    [InlineData("unbound-source")]
    [InlineData("unbound-matrix")]
    [InlineData("routing-overrides")]
    public async Task BindingNeedsExplicitKnownDevicesAndOneRoutingAuthority(string damage)
    {
        var b = new Bench(); var catalog = await b.LoadAsync(); var devices = b.Devices; var matrix = b.Matrix.Definition.Device.Id;
        var source = b.Source.Id; var wiring = new WiringProfileId("k2450");
        switch (damage)
        {
            case "missing-wiring": wiring = new("missing"); break;
            case "unbound-source": source = new("missing"); break;
            case "unbound-matrix": matrix = new("missing"); break;
            case "routing-overrides": devices = b.Bind(catalog); break;
        }
        Assert.Throws<ArgumentException>(() => { catalog.Bind(devices, matrix, [KeyValuePair.Create(wiring, source)], wiring); });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task SampleNumericNameAndOriginalIdCollisionsCannotSelectAnotherSampleSilently()
    {
        var d = Documents(); d.Samples["samples"]!["sample2"]!["sample_id"] = "1";
        var b = new Bench(); var bindings = b.Bind(Parse(d));
        Assert.Throws<FormatException>(() => b.Workspace.Import(Measurement("1", sample: true), bindings));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task CatalogBindingsRejectOverriddenPortsAndCompetingChannelSampleSnapshots()
    {
        var b = new Bench(); var catalog = await b.LoadAsync(); var first = b.Bind(catalog); var second = b.Bind(catalog);
        Assert.Throws<ArgumentException>(() => new ExperimentImportBindings(b.Devices.Instruments, b.Devices.Axes, b.Devices.Preparations,
            samples: first.Samples, channelProfiles: second.ChannelProfiles));
        var wire = first.Samples!.Routing.Wirings[0];
        var changed = new InstrumentWiringProfile(wire.Id, wire.SourceMeter, new(1, 8), wire.SourceMinus, wire.SensePlus, wire.SenseMinus);
        var changedRouting = new RoutingConfiguration(b.Matrix.Definition.Device.Id, catalog.Channels, [changed], catalog.Samples);
        Assert.Throws<ArgumentException>(() => new ImportChannelRouting(catalog, changedRouting, wire.Id, TimeSpan.Zero));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockMatrixSwitch Matrix { get; }
        public NotificationHub Hub { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public ExperimentImportBindings Devices { get; }
        public Bench(bool twoWire = false, FileRunArchive? archive = null)
        {
            var hub = Hub; var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var manager = new InstrumentManager(Source, hub);
            var matrixId = new InstrumentId("matrix");
            Matrix = new(new(new(matrixId, "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 8, 10)], [Source.Id]));
            var field = MockEnvironmentCatalog.Field(); var registry = new DeviceRegistry([manager, new MatrixSwitchSession(Matrix, hub), new AxisControllerSession(field, hub)], leases);
            Runs = new(manager, leases, hub, archive is null ? null : [archive], deviceRegistry: registry);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter(), new PpmsRoutingProfileImporter());
            var preparation = new SourcePreparation(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, twoWire ? SensingMode.TwoWire : SensingMode.FourWire, 1, true));
            Devices = new([KeyValuePair.Create("k2450", Source.Id), KeyValuePair.Create("k3706", matrixId)], [KeyValuePair.Create("field", field.Definition)], [preparation]);
        }
        public Task<RoutingProfileCatalog> LoadAsync() => Workspace.LoadRoutingProfilesAsync(Fixture("channel_profiles.json"), Fixture("samples.json"), Fixture("instrument_ports.json"), Token);
        public ExperimentImportBindings Bind(RoutingProfileCatalog catalog, string wiring = "k2450") => catalog.Bind(Devices, Matrix.Definition.Device.Id, [KeyValuePair.Create(new WiringProfileId(wiring), Source.Id)], new(wiring));
    }
}

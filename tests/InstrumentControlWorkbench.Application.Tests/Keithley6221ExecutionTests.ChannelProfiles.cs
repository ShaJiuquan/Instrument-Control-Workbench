using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Fact]
    public async Task PublishedDeviceAndThreeProfileFilesExecuteOwnedAndManagedIndependentChannels()
    {
        var root = AppContext.BaseDirectory;
        var devices = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(root, "devices", "k6221-meter-matrix-devices.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(devices, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
        var profiles = await new PpmsRoutingProfileImporter().LoadAsync(Path.Combine(root, "profiles", "channel_profiles.json"), Path.Combine(root, "profiles", "samples.json"), Path.Combine(root, "profiles", "instrument_ports.json"), Token);
        var preparation = new CurrentSourcePreparation(new("current-source"), new(new(1e-5, PhysicalUnit.Ampere), new(100, PhysicalUnit.Volt)));
        var bindings = profiles.Bind(runtime.CreateImportBindings(currentSourcePreparations: [preparation]), new("matrix"),
            [KeyValuePair.Create(new WiringProfileId("k6221_k2182"), preparation.Device)], new("k6221_k2182"), channelVoltageMeter: new("nanovoltmeter"));
        var plan = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(root, "plans", "ppms-current-channel-import.json"), bindings, Token);
        Assert.Empty(plan.Preparations); Assert.Single(plan.CurrentSourcePreparations); var runs = new MeasurementRunService(runtime.Registry, hub);
        await runs.StartPlanAsync(plan, Token); var rows = runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count);
        var first = Assert.Single(rows[0].IndependentResistance); Assert.Equal(new[] { new MatrixCrossPoint(1, 5, 1), new(1, 6, 2), new(1, 7, 3), new(1, 8, 4) }, first.Route!.Closed);
        var second = Assert.Single(rows[1].IndependentResistance); Assert.Equal(new[] { new MatrixCrossPoint(1, 5, 7), new(1, 6, 8), new(1, 7, 9), new(1, 8, 10) }, second.Route!.Closed);
        Assert.Equal(second.Route, Assert.Single(rows[2].IndependentResistance).Route); Assert.Equal(-2e-5, Assert.Single(rows[2].IndependentResistance).Current.Value);
        Assert.False(runs.HasActiveRun); Assert.True((await runtime.Registry.GetStatusAsync(preparation.Device, Token)).SafetyConfirmed);
        Assert.True((await runtime.Registry.GetStatusAsync(new("matrix"), Token)).SafetyConfirmed);
    }

    [Theory]
    [InlineData("channel1")]
    [InlineData("CH1")]
    [InlineData(" 1 ")]
    [InlineData("legacy-channel")]
    [InlineData("contacts")]
    [InlineData("__active__")]
    public async Task AutomaticIndependentChannelUsesFrozenProfilesAndExplicitSourceMeterRoles(string query)
    {
        await using var b = new Bench(); var catalog = CurrentChannelCatalog(); var bindings = BindCurrentChannels(b, catalog);
        Assert.Empty(bindings.Channels); Assert.Single(bindings.CurrentSourcePreparations); Assert.Single(bindings.StandaloneVoltageMeters);
        var plan = new PpmsExperimentPlanImporter().Parse(ResistanceJson(new() { ["channel"] = query, ["settle_s"] = 0 }), bindings);
        var document = new JsonExperimentPlanDocument(); plan = document.Parse(document.Serialize(plan));
        Assert.NotNull(plan.Routing); Assert.Equal("source", Assert.Single(plan.Routing.Wirings).SourceMeter.Value);
        Assert.Equal("Authored independent terminals", Assert.Single(plan.Routing.Wirings).Label);
        await b.Runs.StartPlanAsync(plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); var evidence = Assert.Single(row.IndependentResistance);
        Assert.Equal(b.Route, evidence.Route); Assert.Equal(b.Meter.Descriptor.Id, evidence.Meter);
        Assert.Equal(CurrentSourceModel.Keithley6221, evidence.SourceConfiguration.Model); Assert.Equal(100, evidence.Resistance.Value, 8);
        Assert.Equal(3, row.Environment.Count); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task AutomaticCurrentScanInheritsParentChannelAndRetainsItsExcitation()
    {
        await using var b = new Bench(); var scan = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5, -2e-5), ["channel"] = "contacts", ["settle_s"] = 0, ["compliance_v"] = 100, ["source_range_a"] = 20e-6 },
            ["children"] = new JsonArray(JsonNode.Parse(ResistanceJson(new() { ["current_a"] = "parent axis", ["settle_s"] = 20 })))
        };
        var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(1, b.Source.Enables);
        Assert.All(b.Runs.LatestDataSet.Rows, row =>
        {
            var e = Assert.Single(row.IndependentResistance); Assert.Equal(b.Route, e.Route);
            Assert.Equal(row.Values[new("ppms.root.axis")], e.Current); Assert.Equal(100, e.SourceConfiguration.Compliance);
        });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task AutomaticCurrentCommandRoutesWithoutAssumingSmuSensing()
    {
        await using var b = new Bench(); var command = new JsonObject
        {
            ["type"] = "command",
            ["name"] = "set_current",
            ["params"] = new JsonObject { ["target_a"] = -1e-5, ["channel"] = "contacts", ["compliance_v"] = 100 }
        };
        var plan = new PpmsExperimentPlanImporter().Parse(command.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        Assert.DoesNotContain("k2450.", new JsonExperimentPlanDocument().Serialize(plan)); await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(-1e-5, (await b.Source.GetSnapshotAsync(Token)).Settings!.Current.Value);
        Assert.Equal(1, b.Source.Enables); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task MultipleAutomaticChannelsPreserveTheirRoutesInWideArchiveAndCsv()
    {
        var folder = Path.Combine(Path.GetTempPath(), "current-channels-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(writer);
            var nodes = new JsonArray(JsonNode.Parse(ResistanceJson(new() { ["channel"] = "ch1", ["settle_s"] = 0 })), JsonNode.Parse(ResistanceJson(new() { ["channel"] = "ch2", ["settle_s"] = 0 })));
            var sequence = new JsonObject { ["type"] = "sequence", ["children"] = nodes };
            var imported = new PpmsExperimentPlanImporter().Parse(sequence.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
            var acquisitions = Assert.IsType<SequenceStep>(imported.Root).Children;
            var plan = new ExperimentPlan(imported.Name, new MeasurementGroupStep(new("point"), [new("first", acquisitions[0]), new("second", acquisitions[1])]),
                routing: imported.Routing, currentSourcePreparations: imported.CurrentSourcePreparations);
            var codec = new JsonExperimentPlanDocument(); await codec.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token);
            await b.Runs.StartPlanAsync(await codec.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); var first = Assert.Single(rows[0].IndependentResistance); var second = Assert.Single(rows[1].IndependentResistance);
            Assert.Equal(b.Route, first.Route); Assert.NotEqual(first.Route, second.Route); Assert.Equal(2, b.Source.Enables);
            Assert.NotNull(rows[2].MeasurementGroup); Assert.Equal(new[] { first, second }, rows[2].IndependentResistance);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token);
            Assert.Equal(first, Assert.Single(stored.DataSet.Rows[0].IndependentResistance)); Assert.Equal(second, Assert.Single(stored.DataSet.Rows[1].IndependentResistance));
            Assert.Equal(rows[2].IndependentResistance, stored.DataSet.Rows[2].IndependentResistance);
            Assert.Equal("legacy-channel", stored.DataSet.Plan!.Routing!.Channels[0].OriginalId);
            await fresh.ExportCsvAsync(stored.DataSet.Id, Path.Combine(folder, "data.csv"), Token);
            Assert.Contains("independent_resistance_json", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
            await b.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("meter")]
    [InlineData("missingMeter")]
    [InlineData("channel")]
    public async Task MismatchedAutomaticRolesOrModeRejectBeforeConnection(string fault)
    {
        await using var b = new Bench(); var catalog = CurrentChannelCatalog();
        if (fault == "mode") catalog = new(catalog.ActiveChannel, catalog.ActiveSample, catalog.Channels, catalog.Samples, catalog.Wirings, [KeyValuePair.Create("volt_dc", new WiringProfileId("circuit"))]);
        var bindings = BindCurrentChannels(b, catalog, fault != "missingMeter");
        var p = new JsonObject { ["channel"] = fault == "channel" ? "missing" : "ch1" };
        if (fault == "meter") p["meter_instrument"] = "k6221";
        Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(ResistanceJson(p), bindings));
        Assert.Equal(0, b.Source.Connections); Assert.False((await b.Meter.GetSnapshotAsync(Token)).Connected);
        Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task RouteDriftDuringAutomaticAcquisitionRejectsRowAndStopsExcitation()
    {
        await using var b = new Bench(); b.Meter.OnRead = token => b.Matrix.OpenAsync([b.Route.Closed[0]], token);
        var plan = new PpmsExperimentPlanImporter().Parse(ResistanceJson(new() { ["channel"] = "ch1", ["settle_s"] = 0 }), BindCurrentChannels(b, CurrentChannelCatalog()));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task IndependentK2450CatalogRoutingDoesNotTreatItsSmuSenseModeAsExternalMeterGeometry()
    {
        await using var b = new Bench(); var source = new MockKeithley2450SourceMeter(new("source"), "Independent SMU excitation");
        var registry = new DeviceRegistry([new InstrumentManager(source, b.Hub), new Keithley2182MeterSession(b.Meter, b.Hub), new MatrixSwitchSession(b.Matrix, b.Hub)], b.Leases);
        var devices = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k6221"] = source.Id, ["k2182"] = b.Meter.Descriptor.Id, ["k3706"] = b.Matrix.Definition.Device.Id }, [],
            [new(source.Id, SourceFunction.Current, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.FourWire, 1, true))], standaloneVoltageMeters: [b.Meter.Descriptor.Id]);
        var bindings = CurrentChannelCatalog().Bind(devices, b.Matrix.Definition.Device.Id, [KeyValuePair.Create(new WiringProfileId("circuit"), source.Id)], new("circuit"), channelVoltageMeter: b.Meter.Descriptor.Id);
        var scan = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5), ["channel"] = "contacts", ["settle_s"] = 0 },
            ["children"] = new JsonArray(JsonNode.Parse(ResistanceJson(new())))
        };
        var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), bindings); var runs = new MeasurementRunService(registry, b.Hub);
        await runs.StartPlanAsync(plan, Token); var e = Assert.Single(Assert.Single(runs.LatestDataSet!.Rows).IndependentResistance);
        Assert.Equal(CurrentSourceModel.Keithley2450, e.SourceConfiguration.Model); Assert.Equal(b.Route, e.Route);
        Assert.False((await source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.Equal(0, b.Source.Connections);
    }

    [Fact]
    public async Task ManagedAutomaticChannelCannotSwitchRelaysUnderActiveExcitation()
    {
        await using var b = new Bench(); var scan = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_current",
            ["params"] = new JsonObject { ["points"] = new JsonArray(1e-5), ["channel"] = "ch1", ["settle_s"] = 0 },
            ["children"] = new JsonArray(JsonNode.Parse(ResistanceJson(new() { ["channel"] = "ch2" })))
        };
        var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), BindCurrentChannels(b, CurrentChannelCatalog()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
        Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task FailedAutomaticChannelStopRetainsSourceMeterAndMatrixUntilFreshRecovery()
    {
        await using var b = new Bench(); b.Meter.OnRead = _ => { b.Source.RefuseStop = true; return Task.CompletedTask; };
        var plan = new PpmsExperimentPlanImporter().Parse(ResistanceJson(new() { ["channel"] = "contacts", ["settle_s"] = 0 }), BindCurrentChannels(b, CurrentChannelCatalog()));
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.True(b.Runs.HasActiveRun);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.All(b.Registry.Devices, device => Assert.True(b.Leases.IsLeased(device.Id, DateTimeOffset.UtcNow.AddYears(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id, new(0, PhysicalUnit.Ampere), Token));
        b.Source.RefuseStop = false; b.Meter.OnRead = null; await b.Runs.RecoverSafetyAsync(Token, reconnect: true);
        Assert.False(b.Runs.HasActiveRun); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    private static RoutingProfileCatalog CurrentChannelCatalog()
    {
        var first = new ChannelProfile(new("channel1"), "CH1", [new(new("i_plus"), 1, 1), new(new("i_minus"), 1, 2), new(new("v_plus"), 1, 1), new(new("v_minus"), 1, 2)], originalId: "legacy-channel", alias: "contacts");
        var second = new ChannelProfile(new("channel2"), "CH2", [new(new("i_plus"), 1, 2), new(new("i_minus"), 1, 1), new(new("v_plus"), 1, 2), new(new("v_minus"), 1, 1)]);
        var sample = new SampleProfile(new("sample1"), "Two terminal fixture", SampleTopology.TwoWire, [new(new("A"), first.Id, new("i_plus")), new(new("B"), first.Id, new("i_minus"))]);
        var wiring = new MatrixWiringLayout(new("circuit"), "Authored independent terminals", new(1, 1), new(1, 2), new(1, 3), new(1, 4));
        return new(first.Id, sample.Id, [first, second], [sample], [wiring], [KeyValuePair.Create("curr_dc", wiring.Id)]);
    }
    private static ExperimentImportBindings BindCurrentChannels(Bench b, RoutingProfileCatalog catalog, bool meter = true)
    {
        var devices = new ExperimentImportBindings(new Dictionary<string, InstrumentId> { ["k6221"] = b.Source.Descriptor.Id, ["k2182"] = b.Meter.Descriptor.Id, ["k3706"] = b.Matrix.Definition.Device.Id }, [],
            standaloneVoltageMeters: [b.Meter.Descriptor.Id], currentSourcePreparations: [b.Preparation]);
        return catalog.Bind(devices, b.Matrix.Definition.Device.Id, [KeyValuePair.Create(new WiringProfileId("circuit"), b.Source.Descriptor.Id)], new("circuit"), channelVoltageMeter: meter ? b.Meter.Descriptor.Id : null);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
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

public sealed class DeviceConfigurationWorkspaceTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "devices", name);
    private static DeviceConfigurationWorkspace Workspace() => new(new JsonDeviceConfigurationDocument(), new PpmsDeviceConfigurationImporter());
    private static Task<ValidatedDeviceConfiguration> Load() => Workspace().LoadAsync(Fixture("workbench-devices.json"), Token);
    [Fact]
    public async Task NativeFixtureBuildsIndependentInstancesWithFrozenNamesCardsAndAliasesWithoutConnection()
    {
        var document = await Load(); var catalog = Workspace().Validate(document.Text).Catalog;
        Assert.Equal(document.Text, Workspace().Inspect(catalog).Text); Assert.Equal(7, catalog.Devices.Count);
        var hub = new NotificationHub(); using var events = hub.Subscribe(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), leases, hub);
        Assert.False(events.Reader.TryRead(out _));
        Assert.Equal(6, runtime.Registry.Devices.Count); Assert.Equal(2, runtime.Sources.Count); Assert.Equal("Primary specimen SMU", runtime.PrimarySource.Descriptor.DisplayName);
        Assert.Equal(new InstrumentId("smu-primary"), runtime.CreateImportBindings().Instruments["k2450"]); Assert.False(runtime.CreateImportBindings().Instruments.ContainsKey("k2182"));
        Assert.Equal(new InstrumentId("magnet-1"), runtime.CreateImportBindings().Axes["field"].Device.Id);
        Assert.Equal(2, runtime.Registry.GetMatrixDefinition(new("matrix-1")).GuardedSources.Count);
        foreach (var device in runtime.Registry.Devices) { Assert.Equal(DriverKind.Simulation, device.DriverKind); Assert.False((await runtime.Registry.GetStatusAsync(device.Id, Token)).Connected); Assert.False(leases.IsLeased(device.Id, DateTimeOffset.UtcNow)); }
        await runtime.Registry.ConnectAsync(new("smu-secondary"), Token); Assert.False((await runtime.Registry.GetStatusAsync(new("smu-primary"), Token)).Connected);
        await runtime.Registry.DisconnectAsync(new("smu-secondary"), Token);
    }
    [Fact]
    public async Task AxisOnlySettingsManageDevicesWithoutCreatingAnUnusedSourceMeter()
    {
        var field = new DeviceConfiguration(new("field-only"), "SIM-FIELD", "Independent magnet", ConfiguredDeviceCategory.Field, new SimulationDeviceConnection());
        var w = Workspace(); var document = w.Inspect(new(null, [field], [KeyValuePair.Create("field", field.Id)]));
        var runtime = new ConfiguredDeviceRuntime(w.Validate(document.Text).Catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new());
        Assert.Null(runtime.Configuration.PrimarySource); Assert.Empty(runtime.Sources); Assert.Single(runtime.Registry.Devices);
        Assert.Throws<InvalidOperationException>(() => runtime.PrimarySource);
        await runtime.Registry.ConnectAsync(field.Id, Token); await runtime.Registry.WriteAsync(field.Id, new("field.target"), new(1, PhysicalUnit.Tesla), Token);
        Assert.Equal(PhysicalUnit.Tesla, (await runtime.Registry.ReadAsync(field.Id, new("field.readback"), Token)).Unit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Registry.DisconnectAsync(field.Id, Token));
        var scope = await runtime.Registry.AcquireRunAsync([field.Id], InstrumentControlWorkbench.Core.Data.RunId.New(), Token);
        try { await scope.ConnectAllAsync(Token); }
        finally { Assert.Empty(await scope.StopAllAsync(CancellationToken.None)); await scope.ReleaseAsync(CancellationToken.None); }
        await runtime.Registry.DisconnectAsync(field.Id, Token);
        var legacy = new PpmsDeviceConfigurationImporter().Parse("{\"devices\":{\"rotator\":{\"transport\":\"serial\",\"port\":\"EXAMPLE-SERIAL\",\"timeout_s\":5}}}", null);
        Assert.Null(legacy.PrimarySource); Assert.Single(legacy.Devices);
    }
    [Fact]
    public async Task NativeTimeoutsRoundTripExactlyAtRuntimeTickPrecision()
    {
        var w = Workspace(); var c = (await Load()).Catalog; var template = c.Devices.Single(d => d.Id.Value == "future-meter");
        var timeout = TimeSpan.FromTicks(150_000_001);
        var changed = w.Update(c, new(template.Id, template.Model, template.DisplayName, template.Category, new TcpDeviceConnection("localhost", 5025, "\n", timeout), false));
        var restored = w.Validate(changed.Text).Catalog;
        Assert.Equal(timeout, Assert.IsType<TcpDeviceConnection>(restored.Devices.Single(d => d.Id == template.Id).Connection).Timeout);
        Assert.Equal(changed.Text, w.Inspect(restored).Text);
    }
    [Fact]
    public async Task ExplicitTypedEditsRetainHardwareTemplatesAndRejectDanglingOwners()
    {
        var w = Workspace(); var original = await Load(); var c = original.Catalog; var source = c.Devices[0];
        var changed = w.Update(c, new(source.Id, source.Model, "Next startup name", source.Category, source.Connection));
        changed = w.SelectPrimary(changed.Catalog, new("smu-secondary")); changed = w.SetAlias(changed.Catalog, "k2450", new("smu-secondary"));
        changed = w.SetAlias(changed.Catalog, "k2182", null); changed = w.Delete(changed.Catalog, new("future-meter"));
        Assert.Equal(6, changed.Catalog.Devices.Count); Assert.Equal("Primary specimen SMU", c.Devices[0].DisplayName); Assert.Equal(new InstrumentId("smu-secondary"), changed.Catalog.PrimarySource);
        var added = new DeviceConfiguration(new("spare"), "Unimplemented", "Offline template", ConfiguredDeviceCategory.Meter, new VisaDeviceConnection("GPIB0::19::INSTR"), false);
        changed = w.Update(changed.Catalog, added); changed = w.Delete(changed.Catalog, added.Id);
        Assert.Throws<ArgumentException>(() => w.Delete(c, source.Id)); Assert.Throws<ArgumentException>(() => w.Delete(c, new("future-meter")));
        Assert.Throws<ArgumentException>(() => w.Update(c, new(new("smu-secondary"), "K2450", "Disabled guard", ConfiguredDeviceCategory.SourceMeter, new SimulationDeviceConnection(), false)));
        Assert.Throws<ArgumentException>(() => w.SetAlias(c, "field", source.Id)); Assert.Equal(original.Text, w.Inspect(c).Text);
    }
    [Fact]
    public async Task PpmsImportPreservesAllEightHardwareTransportsAndRequiresExplicitCardConfiguration()
    {
        var w = Workspace(); var document = await w.ImportAsync(Fixture("ppms-connections.json"), new("k2450"), Token); var restored = w.Validate(document.Text).Catalog;
        Assert.Equal(8, restored.Devices.Count); Assert.All(restored.Devices, d => Assert.Equal(DriverKind.Hardware, d.DriverKind));
        var tcp = Assert.IsType<TcpDeviceConnection>(restored.Devices.Single(d => d.Id.Value == "k2450").Connection); Assert.Equal("\r\n", tcp.Terminator); Assert.Equal(TimeSpan.FromSeconds(15), tcp.Timeout);
        Assert.IsType<VisaDeviceConnection>(restored.Devices.Single(d => d.Id.Value == "sr830").Connection); Assert.Equal(TimeSpan.FromSeconds(2.5), Assert.IsType<SerialDeviceConnection>(restored.Devices.Single(d => d.Id.Value == "rotator").Connection).Timeout);
        Assert.Null(restored.Devices.Single(d => d.Category == ConfiguredDeviceCategory.Matrix).Matrix); Assert.Equal(new InstrumentId("rotator"), restored.Aliases["angle"]);
        var factory = new RecordingFactory(); Assert.Throws<NotSupportedException>(() => new ConfiguredDeviceRuntime(restored, factory, new(TimeSpan.FromSeconds(5)), new())); Assert.Equal(0, factory.Created);
    }
    [Theory]
    [InlineData("hardware")]
    [InlineData("unsupported")]
    [InlineData("cards")]
    public async Task StartupValidatesTheEntireEnabledSetBeforeConstructingAnyDevice(string failure)
    {
        var w = Workspace(); var c = (await Load()).Catalog; var d = c.Devices.Single(p => p.Id.Value == "matrix-1");
        var changed = w.Update(c, new(d.Id, failure == "unsupported" ? "Unknown matrix" : d.Model, d.DisplayName, d.Category,
            failure == "hardware" ? new TcpDeviceConnection("127.0.0.1", 5025, "\n", TimeSpan.FromSeconds(1)) : d.Connection, matrix: failure == "cards" ? null : d.Matrix));
        var factory = new RecordingFactory(); Assert.Throws<NotSupportedException>(() => new ConfiguredDeviceRuntime(changed.Catalog, factory, new(TimeSpan.FromSeconds(5)), new())); Assert.Equal(0, factory.Created);
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("label")]
    [InlineData("axis-unit")]
    [InlineData("matrix-guard")]
    public async Task StartupRejectsAnAdapterThatMisrepresentsItsConfiguration(string failure)
    {
        var c = (await Load()).Catalog; var factory = new RecordingFactory(failure);
        Assert.Throws<ArgumentException>(() => new ConfiguredDeviceRuntime(c, factory, new(TimeSpan.FromSeconds(5)), new()));
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("primary")]
    [InlineData("timeout")]
    [InlineData("transport")]
    [InlineData("missing-card")]
    [InlineData("guard")]
    [InlineData("numeric-category")]
    public async Task InvalidSavesPreservePreviousBytesAndLeaveNoTemporaryFiles(string failure)
    {
        var folder = Path.Combine(Path.GetTempPath(), "device-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = Workspace(); var original = await Load(); var path = Path.Combine(folder, "devices.json"); await w.SaveAsync(path, original.Text, Token); var root = JsonNode.Parse(original.Text)!;
            switch (failure)
            {
                case "unknown": root["autoConnect"] = true; break;
                case "version": root["version"] = 2; break;
                case "primary": root["primarySource"] = "missing"; break;
                case "timeout": root["devices"]![0]!["connection"] = JsonNode.Parse("{\"transport\":\"tcp\",\"host\":\"localhost\",\"port\":5025,\"terminator\":\"\\n\",\"timeoutSeconds\":0}"); break;
                case "transport": root["devices"]![0]!["connection"]!["transport"] = "auto-mock-fallback"; break;
                case "missing-card": ((JsonObject)root["devices"]![5]!["matrix"]!["cards"]![0]!).Remove("rows"); break;
                case "guard": root["devices"]![5]!["matrix"]!["guardedSources"]![0] = "missing"; break;
                case "numeric-category": root["devices"]![0]!["category"] = "0"; break;
            }
            var text = root.ToJsonString(); if (failure == "duplicate") text = text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
            Assert.NotNull(await Record.ExceptionAsync(() => w.SaveAsync(path, text, Token))); Assert.Equal(original.Text, await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(folder, ".*.tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("rotator-tcp")]
    [InlineData("scpi-serial")]
    [InlineData("unknown")]
    [InlineData("field")]
    [InlineData("precision")]
    public void PpmsInvalidEntriesRejectWithoutDefaultsOrFallback(string failure)
    {
        var root = JsonNode.Parse(File.ReadAllText(Fixture("ppms-connections.json")))!; var devices = (JsonObject)root["devices"]!;
        switch (failure)
        {
            case "rotator-tcp": devices["rotator"] = devices["k2450"]!.DeepClone(); break;
            case "scpi-serial": devices["k2450"] = devices["rotator"]!.DeepClone(); break;
            case "unknown": devices["unexpected"] = devices["sr830"]!.DeepClone(); devices.Remove("k2182"); break;
            case "field": devices["k2450"]!["extra"] = 0; break;
            case "precision": devices["k2450"]!["timeout_s"] = 0.00000001m; break;
        }
        Assert.Throws<FormatException>(() => new PpmsDeviceConfigurationImporter().Parse(root.ToJsonString(), new("k2450")));
    }
    [Fact]
    public async Task FileBoundarySupportsBomStrictUtf8PrecancellationAndRenameFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "device-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var w = Workspace(); var original = await Load(); var path = Path.Combine(folder, "devices.json");
            await File.WriteAllTextAsync(path, "\uFEFF" + original.Text, new UTF8Encoding(false)); Assert.Equal(original.Text, (await w.LoadAsync(path, Token)).Text);
            var before = await File.ReadAllBytesAsync(path);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.LoadAsync("missing", cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.SaveAsync(path, original.Text, cancelled.Token)); Assert.Equal(before, await File.ReadAllBytesAsync(path));
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]); await Assert.ThrowsAsync<DecoderFallbackException>(() => w.LoadAsync(path, Token));
            var directory = Path.Combine(folder, "target"); Directory.CreateDirectory(directory); Assert.NotNull(await Record.ExceptionAsync(() => w.SaveAsync(directory, original.Text, Token))); Assert.Empty(Directory.GetFiles(folder, ".*.tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("bytes")]
    [InlineData("depth")]
    [InlineData("values")]
    public void ParserBudgetsBoundBothNativeAndPpmsDocuments(string budget)
    {
        var text = budget switch { "bytes" => new string(' ', JsonDeviceConfigurationDocument.MaximumBytes + 1), "depth" => new string('[', 17) + "0" + new string(']', 17), _ => "[" + string.Join(',', Enumerable.Repeat("0", 50_001)) + "]" };
        Assert.Throws<FormatException>(() => new JsonDeviceConfigurationDocument().Parse(text)); Assert.Throws<FormatException>(() => new PpmsDeviceConfigurationImporter().Parse(text, new("k2450")));
    }
    [Fact]
    public async Task FreshSavedRuntimeImportsRunsAndArchivesThenEditsOnlyAffectNextStartup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "configured-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = Workspace(); var original = await Load(); var path = Path.Combine(folder, "devices.json"); await w.SaveAsync(path, original.Text, Token);
            var loaded = (await Workspace().LoadAsync(path, Token)).Catalog; var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            var runtime = new ConfiguredDeviceRuntime(loaded, new ConfiguredDeviceFactory(), leases, hub);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var runs = new MeasurementRunService(runtime.PrimarySource, leases, hub, [archive], deviceRegistry: runtime.Registry);
            var plan = await ImportPlan(runtime, runs); var edited = w.SetAlias(w.SelectPrimary(loaded, new("smu-secondary")).Catalog, "k2450", new("smu-secondary"));
            using var events = hub.Subscribe(); var running = runs.StartPlanAsync(plan.Plan, Token);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.DataRowReceived) { }
                runs.Pause(); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
                Assert.True(runs.HasActiveRun); await w.SaveAsync(path, edited.Text, Token);
                Assert.Equal(new InstrumentId("smu-primary"), runtime.Configuration.PrimarySource); Assert.Equal(new InstrumentId("smu-primary"), runtime.PrimarySource.InstrumentId);
                Assert.Equal(new InstrumentId("smu-primary"), runtime.CreateImportBindings().Instruments["k2450"]); runs.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { if (!running.IsCompleted) { runs.Abort(); await running; } }
            Assert.Equal(28, runs.LatestDataSet!.Rows.Count); var stored = await archive.LoadAsync(runs.LatestDataSet.Id, Token); Assert.Equal(28, stored.DataSet.Rows.Count);
            Assert.All(runs.LatestDataSet.Rows.Where(r => r.Routing is not null), row => Assert.Equal(new InstrumentId("smu-primary"), row.Routing!.SourceMeter));
            var freshHub = new NotificationHub(); var freshLeases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            var fresh = new ConfiguredDeviceRuntime((await w.LoadAsync(path, Token)).Catalog, new ConfiguredDeviceFactory(), freshLeases, freshHub);
            Assert.Equal(new InstrumentId("smu-secondary"), fresh.PrimarySource.InstrumentId); var freshRuns = new MeasurementRunService(fresh.PrimarySource, freshLeases, freshHub, deviceRegistry: fresh.Registry);
            await freshRuns.StartPlanAsync((await ImportPlan(fresh, freshRuns)).Plan, Token); Assert.Equal(28, freshRuns.LatestDataSet!.Rows.Count);
            foreach (var source in runtime.Sources.Values) Assert.True((await source.GetDeviceStatusAsync(Token)).SafetyConfirmed);
            var resources = await runtime.Registry.AcquireRunAsync([new("matrix-1"), new("smu-primary"), new("smu-secondary")], InstrumentControlWorkbench.Core.Data.RunId.New(), Token);
            try { await resources.ConnectAllAsync(Token); await resources.VerifyMatrixRouteAsync(new("matrix-1"), [], Token); }
            finally { Assert.Empty(await resources.StopAllAsync(CancellationToken.None)); await resources.ReleaseAsync(CancellationToken.None); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task<ValidatedPlanDocument> ImportPlan(ConfiguredDeviceRuntime runtime, MeasurementRunService runs)
    {
        var source = runtime.PrimarySource.InstrumentId;
        var devices = runtime.CreateImportBindings([new(source, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
        var catalog = await new PpmsRoutingProfileImporter().LoadAsync(Path.Combine(AppContext.BaseDirectory, "profiles", "channel_profiles.json"), Path.Combine(AppContext.BaseDirectory, "profiles", "samples.json"), Path.Combine(AppContext.BaseDirectory, "profiles", "instrument_ports.json"), Token);
        var binding = catalog.Bind(devices, new("matrix-1"), [KeyValuePair.Create(new WiringProfileId("k2450"), source)], new("k2450"));
        return await new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter()).ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-samples-import.json"), binding, Token);
    }
    private sealed class RecordingFactory(string? failure = null) : IConfiguredDeviceFactory
    {
        private readonly ConfiguredDeviceFactory inner = new(); public int Created { get; private set; }
        public void Validate(DeviceConfiguration configuration) => inner.Validate(configuration);
        public ConfiguredDevicePorts Create(DeviceConfiguration c)
        {
            Created++;
            if (failure == "identity" && c.Category == ConfiguredDeviceCategory.SourceMeter) return new(new MockKeithley2450SourceMeter(new("wrong"), c.DisplayName));
            if (failure == "label" && c.Category == ConfiguredDeviceCategory.SourceMeter) return new(new MockKeithley2450SourceMeter(c.Id, "wrong label"));
            if (failure == "axis-unit" && c.Category == ConfiguredDeviceCategory.Field) return new(MockEnvironmentCatalog.Temperature(id: c.Id, displayName: c.DisplayName));
            if (failure == "matrix-guard" && c.Category == ConfiguredDeviceCategory.Matrix)
                return new(new MockMatrixSwitch(new(new(c.Id, c.Model, c.DisplayName, DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), c.Matrix!.Cards, [new("smu-primary")])));
            return inner.Create(c);
        }
    }
}

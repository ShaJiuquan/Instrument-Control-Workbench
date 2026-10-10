using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class DeviceConfigurationStartupTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Task<DeviceConfigurationCatalog> Catalog(string file = "axes-only-devices.json") => new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "devices", file), Token);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInitialConfigurationOnlySeedsMissingFileWithoutWritingOrConnecting(bool missingDirectory)
    {
        var folder = Path.Combine(Path.GetTempPath(), "startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!missingDirectory) Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "devices.json"); var seed = await Catalog(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); var factory = new Factory();
            var started = await new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), factory, leases, new()).StartAsync(path, seed, Token);
            Assert.Equal(DeviceConfigurationOrigin.ExplicitInitialConfiguration, started.Origin); Assert.False(File.Exists(path)); Assert.Equal(!missingDirectory, Directory.Exists(folder));
            Assert.Empty(started.Devices.Sources); Assert.Equal(3, factory.Created);
            foreach (var device in started.Devices.Registry.Devices)
            { Assert.False((await started.Devices.Registry.GetStatusAsync(device.Id, Token)).Connected); Assert.False(leases.IsLeased(device.Id, DateTimeOffset.UtcNow)); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task MissingFileWithoutExplicitInitialConfigurationDoesNotCreateAWorkbench()
    {
        var factory = new Factory(); var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "devices.json");
        var error = await Record.ExceptionAsync(() => new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), factory, new(TimeSpan.FromSeconds(5)), new()).StartAsync(path, null, Token));
        Assert.True(error is FileNotFoundException or DirectoryNotFoundException); Assert.Equal(0, factory.Created);
    }
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"instrument-workbench-devices\",\"format\":\"instrument-workbench-devices\",\"version\":1}")]
    public async Task ExistingMalformedFileNeverFallsBackToSuppliedSimulation(string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "startup-invalid-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "devices.json"); await File.WriteAllTextAsync(path, text); var factory = new Factory();
            var seed = await Catalog();
            var error = await Record.ExceptionAsync(() => new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), factory, new(TimeSpan.FromSeconds(5)), new()).StartAsync(path, seed, Token));
            Assert.NotNull(error); Assert.Equal(0, factory.Created); Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task UnsupportedSavedHardwareRejectsTheWholeSetBeforeConstructingAnyPort()
    {
        var folder = Path.Combine(Path.GetTempPath(), "startup-hardware-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seed = await Catalog(); var bad = new DeviceConfigurationCatalog(null, [.. seed.Devices, new(new("hardware"), "K2450", "Real source", ConfiguredDeviceCategory.SourceMeter, new TcpDeviceConnection("127.0.0.1", 5025, "\n", TimeSpan.FromSeconds(1)))], seed.Aliases);
            var path = Path.Combine(folder, "devices.json"); var doc = new JsonDeviceConfigurationDocument(); await doc.SaveAsync(path, bad, Token); var original = await File.ReadAllTextAsync(path); var factory = new Factory();
            await Assert.ThrowsAsync<NotSupportedException>(() => new DeviceConfigurationStartup(doc, factory, new(TimeSpan.FromSeconds(5)), new()).StartAsync(path, seed, Token));
            Assert.Equal(0, factory.Created); Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task SavedAxesConfigurationExecutesRealPlanAndArchivesWithoutASeedOrSource()
    {
        var folder = Path.Combine(Path.GetTempPath(), "startup-axes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "devices.json"); var doc = new JsonDeviceConfigurationDocument(); await doc.SaveAsync(path, await Catalog(), Token);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var started = await new DeviceConfigurationStartup(doc, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()).StartAsync(path, null, Token, [archive]);
            Assert.Equal(DeviceConfigurationOrigin.SavedFile, started.Origin); Assert.Empty(started.Devices.Sources); Assert.Null(started.Document.Catalog.PrimarySource);
            var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "axes-only-scan.plan.json"), Token);
            await started.Runs.StartPlanAsync(plan, Token); Assert.Equal(6, started.Runs.LatestDataSet!.Rows.Count);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await fresh.LoadAsync(started.Runs.LatestDataSet.Id, Token);
            Assert.Equal(3, saved.DataSet.Instruments.Count); Assert.Equal(MeasurementRunState.Completed, saved.DataSet.State);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task SavedPrimaryControlsLegacyShortcutAndNextStartupSeesEditsWithoutChangingLiveRuntime()
    {
        var folder = Path.Combine(Path.GetTempPath(), "startup-primary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "devices.json"); var seed = await Catalog("workbench-devices.json"); var doc = new JsonDeviceConfigurationDocument(); var workspace = new DeviceConfigurationWorkspace(doc);
            var second = workspace.SelectPrimary(seed, new("smu-secondary")); await doc.SaveAsync(path, second.Catalog, Token);
            var hub = new NotificationHub(); var startup = new DeviceConfigurationStartup(doc, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
            var started = await startup.StartAsync(path, seed, Token); Assert.Equal(new("smu-secondary"), started.Devices.PrimarySource.InstrumentId);
            await workspace.SaveAsync(path, workspace.Inspect(seed).Text, Token);
            await started.Runs.StartIvSweepAsync(new("Source from saved settings", 0, 0.02, 2, 105e-6, 0), Token);
            Assert.Equal(new("smu-secondary"), Assert.Single(started.Runs.LatestDataSet!.Instruments).InstrumentId);
            Assert.False((await started.Devices.Registry.GetStatusAsync(new("smu-primary"), Token)).Connected);
            var restarted = await startup.StartAsync(path, null, Token); Assert.Equal(new("smu-primary"), restarted.Devices.PrimarySource.InstrumentId);
            Assert.Equal(new("smu-secondary"), started.Document.Catalog.PrimarySource); Assert.False(started.Runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrAfterLoadPreventsFactoryConstruction(bool cancelAfterLoad)
    {
        using var cancellation = new CancellationTokenSource(); var factory = new Factory(); var catalog = await Catalog();
        var document = new LoadProbe(catalog, () => cancellation.Cancel()); if (!cancelAfterLoad) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeviceConfigurationStartup(document, factory, new(TimeSpan.FromSeconds(5)), new()).StartAsync("devices.json", catalog, cancellation.Token));
        Assert.Equal(cancelAfterLoad ? 1 : 0, document.Loads); Assert.Equal(0, factory.Created);
    }
    [Fact]
    public async Task NonMissingReadErrorIsVisibleAndDoesNotConstructSeedDrivers()
    {
        var seed = await Catalog(); var factory = new Factory(); var document = new LoadProbe(seed, () => throw new UnauthorizedAccessException("settings access denied"));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DeviceConfigurationStartup(document, factory, new(TimeSpan.FromSeconds(5)), new()).StartAsync("devices.json", seed, Token));
        Assert.Equal("settings access denied", error.Message); Assert.Equal(0, factory.Created);
    }
    [Fact]
    public async Task OneShotListenerEnumerationAndFailureRemainPartOfRealRunFinalization()
    {
        var seed = await Catalog(); var seen = false; var listener = new Listener();
        IEnumerable<IRunResultListener> Listeners() { Assert.False(seen); seen = true; yield return listener; }
        var startup = new DeviceConfigurationStartup(new LoadProbe(seed, () => { }), new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new());
        var started = await startup.StartAsync("devices.json", null, Token, Listeners());
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "axes-only-scan.plan.json"), Token);
        await Assert.ThrowsAsync<IOException>(() => started.Runs.StartPlanAsync(plan, Token)); Assert.Equal(6, listener.Rows);
        Assert.Equal(MeasurementRunState.Completed, started.Runs.LatestDataSet!.State); Assert.Equal(MeasurementRunState.Faulted, started.Runs.LatestOutcome!.State); Assert.False(started.Runs.HasActiveRun);
    }
    [Fact]
    public async Task AuthoredNamesForSourceAxisAndMatrixRemainFrozenInFreshArchiveAfterSettingsEdit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "startup-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = await Catalog("workbench-devices.json"); var path = Path.Combine(folder, "devices.json"); var doc = new JsonDeviceConfigurationDocument(); await doc.SaveAsync(path, catalog, Token);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs"));
            var started = await new DeviceConfigurationStartup(doc, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()).StartAsync(path, null, Token, [archive]);
            var matrixId = catalog.Aliases["k3706"]; var fieldId = catalog.Aliases["field"]; var matrix = started.Devices.Registry.GetMatrixDefinition(matrixId); var card = matrix.Cards[0];
            var plan = new ExperimentPlan("Named source-axis-matrix", new SequenceStep(new("root"),
            [new RouteMatrixStep(new("route"), matrixId, [new(card.Slot, 1, 1)], TimeSpan.Zero),
             new ReadStep(new("read"), [new(new("field"), new(fieldId, started.Devices.Registry.GetAxisDefinition(fieldId).Readback.Id))])]));
            await started.Runs.StartPlanAsync(plan, Token); Assert.Equal(4, started.Runs.LatestDataSet!.Instruments.Count);
            var workspace = new DeviceConfigurationWorkspace(doc);
            var renamed = new DeviceConfigurationCatalog(catalog.PrimarySource, catalog.Devices.Select(d => new DeviceConfiguration(d.Id, d.Model, "Edited " + d.DisplayName, d.Category, d.Connection, d.Enabled, d.Matrix)), catalog.Aliases);
            await workspace.SaveAsync(path, workspace.Inspect(renamed).Text, Token);
            await using var reader = new FileRunArchive(Path.Combine(folder, "runs")); var reopened = await reader.LoadAsync(started.Runs.LatestDataSet.Id, Token);
            Assert.All(reopened.DataSet.Instruments, snapshot => Assert.Equal(catalog.Devices.Single(d => d.Id == snapshot.InstrumentId).DisplayName, snapshot.Configuration["display_name"]));
            var next = await new DeviceConfigurationStartup(doc, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()).StartAsync(path, null, Token);
            Assert.All(next.Devices.Registry.Devices, device => Assert.StartsWith("Edited ", device.DisplayName));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Factory : IConfiguredDeviceFactory
    {
        private readonly ConfiguredDeviceFactory inner = new(); public int Created { get; private set; }
        public void Validate(DeviceConfiguration config) => inner.Validate(config);
        public ConfiguredDevicePorts Create(DeviceConfiguration config) { Created++; return inner.Create(config); }
    }
    private sealed class LoadProbe(DeviceConfigurationCatalog catalog, Action loading) : IDeviceConfigurationDocument
    {
        public int Loads { get; private set; }
        public Task<DeviceConfigurationCatalog> LoadAsync(string path, CancellationToken token) { Loads++; loading(); return Task.FromResult(catalog); }
        public string Serialize(DeviceConfigurationCatalog config) => new JsonDeviceConfigurationDocument().Serialize(config);
        public DeviceConfigurationCatalog Parse(string text) => throw new NotSupportedException();
        public Task SaveAsync(string path, DeviceConfigurationCatalog config, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Listener : IRunResultListener
    {
        public int Rows { get; private set; }
        public Task OnStartedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.CompletedTask;
        public Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { Rows++; return Task.CompletedTask; }
        public Task OnFinalizedAsync(RunDataSetSnapshot data, CancellationToken token) => Task.FromException(new IOException("archive unavailable"));
    }
}

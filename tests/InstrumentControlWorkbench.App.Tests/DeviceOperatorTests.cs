using InstrumentControlWorkbench.App.ViewModels;
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
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class DeviceOperatorTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Fact]
    public async Task DeviceListAndParametersUseConfiguredInstanceNamesKindsAndActualState()
    {
        var b = new Bench(); await using var owner = b.Vm;
        var devices = b.Vm.Devices!; Assert.Equal(2, devices.Devices.Count); Assert.Equal("Named magnet", devices.Devices[0].Name); Assert.Contains("Simulation", devices.Devices[0].Kind);
        Assert.All(devices.Parameters, p => Assert.Equal("—", p.Value)); await devices.ConnectCommand.ExecuteAsync(null);
        Assert.True(devices.Selected!.Connected); Assert.False(devices.HasError); Assert.Contains(devices.Parameters, p => p.Unit == "Tesla" && p.Value == "0");
        Assert.False(devices.ConnectCommand.CanExecute(null)); Assert.True(devices.DisconnectCommand.CanExecute(null));
        await devices.DisconnectCommand.ExecuteAsync(null); Assert.False(devices.Selected.Connected); Assert.True(devices.ConnectCommand.CanExecute(null));
        devices.Selected = devices.Devices[1]; await devices.ConnectCommand.ExecuteAsync(null); Assert.False(devices.HasError);
        Assert.Contains(devices.Parameters, p => p.Unit == "Volt"); Assert.Equal("已回报输出关闭", devices.Selected.Safety);
    }
    [Fact]
    public async Task AxesOnlyWorkbenchHasNoSmuControlsButExecutesItsBoundPlan()
    {
        var b = new Bench(source: false); await using var owner = b.Vm;
        Assert.False(b.Vm.HasPrimarySource); Assert.False(b.Vm.IsMeasurementPage); Assert.True(b.Vm.IsPlanPage);
        Assert.False(b.Vm.StartRunCommand.CanExecute(null)); Assert.False(b.Vm.ConnectCommand.CanExecute(null)); Assert.True(b.Vm.StartPlanCommand.CanExecute(null));
        using var events = b.Hub.Subscribe(); var running = b.Vm.StartPlanCommand.ExecuteAsync(null);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); Assert.False(b.Vm.Devices!.ConnectCommand.CanExecute(null)); Assert.False(b.Vm.Devices.RefreshCommand.CanExecute(null));
            b.Vm.AbortRunCommand.Execute(null); await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Single(b.Runs.LatestDataSet.Instruments); Assert.Empty(b.Runtime.Sources);
            Assert.False(b.Runs.HasActiveRun); Assert.True(b.Vm.Devices.RefreshCommand.CanExecute(null));
        }
        finally { if (!running.IsCompleted) { b.Runs.Abort(); await running; } }
    }
    [Fact]
    public async Task ForeignLeaseRejectsManualConnectionAndCloseKeepsTheWorkbenchAvailableForRetry()
    {
        var b = new Bench(); var id = b.Runtime.Registry.Devices[0].Id;
        await b.Runtime.Registry.ConnectAsync(b.Runtime.Registry.Devices[1].Id, Token);
        b.Leases.AcquireExclusive(id, "foreign", DateTimeOffset.UtcNow);
        try
        {
            await b.Vm.Devices!.ConnectCommand.ExecuteAsync(null); Assert.True(b.Vm.Devices.HasError); Assert.False(b.Vm.Devices.Selected!.Connected);
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Vm.DisposeAsync().AsTask()); Assert.Contains("关闭未完成", b.Vm.OperationError);
            Assert.True(b.Leases.IsLeased(id, DateTimeOffset.UtcNow)); Assert.True(b.Vm.Devices.RefreshCommand.CanExecute(null));
            Assert.False((await b.Runtime.Registry.GetStatusAsync(b.Runtime.Registry.Devices[1].Id, Token)).Connected);
        }
        finally { b.Leases.Release(id, "foreign"); await b.Vm.DisposeAsync(); }
    }
    [Fact]
    public async Task CloseDisconnectsEveryManuallyConnectedDeviceAfterRunFinalization()
    {
        var b = new Bench(); foreach (var item in b.Vm.Devices!.Devices) { b.Vm.Devices.Selected = item; await b.Vm.Devices.ConnectCommand.ExecuteAsync(null); }
        Assert.All(b.Vm.Devices.Devices, item => Assert.True(item.Connected)); await b.Vm.DisposeAsync();
        foreach (var item in b.Runtime.Registry.Devices) Assert.False((await b.Runtime.Registry.GetStatusAsync(item.Id, Token)).Connected);
        Assert.False(b.Vm.Devices.RefreshCommand.CanExecute(null)); Assert.False(b.Vm.StartPlanCommand.CanExecute(null));
    }
    [Fact]
    public async Task NotificationForAnotherDeviceCannotReplaceTheSelectedDeviceOrItsParameterValues()
    {
        var b = new Bench(); await using var owner = b.Vm; var vm = b.Vm.Devices!;
        vm.Selected = vm.Devices[1]; var id = b.Runtime.Registry.Devices[0].Id; var status = await b.Runtime.Registry.ConnectAsync(id, Token);
        vm.ApplyFact(new(WorkbenchNotificationKind.DeviceStateChanged, "field", id, Device: status));
        Assert.True(vm.Devices[0].Connected); Assert.False(vm.Selected!.Connected); Assert.All(vm.Parameters, p => Assert.Equal("—", p.Value));
        vm.Selected = vm.Devices[0]; Assert.Contains(vm.Parameters, p => p.Unit == "Tesla" && p.Value == "0");
    }
    [Fact]
    public async Task SettingsSaveDuringPausedRunRetainsCapturedDevicesAndRequiresRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "device-ui-" + Guid.NewGuid().ToString("N"));
        var b = new Bench(); var doc = new JsonDeviceConfigurationDocument(); var workspace = new DeviceConfigurationWorkspace(doc);
        await using var settings = new DeviceSettingsViewModel(workspace, workspace.Inspect(b.Runtime.Configuration), Path.Combine(folder, "devices.json"));
        await using var owner = b.Vm; using var events = b.Hub.Subscribe(); var running = b.Vm.StartPlanCommand.ExecuteAsync(null);
        try
        {
            await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Vm.PauseRunCommand.Execute(null); await Until(events, WorkbenchNotificationKind.RunPaused);
            var field = b.Runtime.Configuration.Devices[0]; var changed = new DeviceConfiguration(field.Id, field.Model, "Next startup magnet", field.Category, field.Connection);
            settings.DocumentText = workspace.Update(b.Runtime.Configuration, changed).Text; Assert.True(settings.SaveCommand.CanExecute(null)); await settings.SaveCommand.ExecuteAsync(null);
            Assert.False(settings.HasError); Assert.True(settings.RestartRequired); Assert.False(settings.IsDirty); Assert.Equal("Named magnet", b.Runtime.Registry.Devices[0].DisplayName);
            b.Runs.Abort(); await running; Assert.Equal("Named magnet", Assert.Single(b.Runs.LatestDataSet!.Instruments).Configuration["display_name"]);
            var next = await new DeviceConfigurationStartup(doc, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), new()).StartAsync(settings.DocumentPath, null, Token);
            Assert.Equal("Next startup magnet", next.Devices.Registry.Devices[0].DisplayName);
        }
        finally { if (!running.IsCompleted) { b.Runs.Abort(); await running; } if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task InvalidDraftAndFailedLoadPreserveSavedSettingsAndCanBeCorrected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "device-ui-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var b = new Bench(); await using var owner = b.Vm; var workspace = new DeviceConfigurationWorkspace(new JsonDeviceConfigurationDocument()); var initial = workspace.Inspect(b.Runtime.Configuration); var path = Path.Combine(folder, "devices.json");
            await using var settings = new DeviceSettingsViewModel(workspace, initial, path); Assert.False(settings.IsDirty); await settings.SaveCommand.ExecuteAsync(null); var old = await File.ReadAllTextAsync(path);
            settings.DocumentText = "bad JSON"; Assert.False(settings.IsValidated); await settings.SaveCommand.ExecuteAsync(null); Assert.True(settings.HasError); Assert.Equal(old, await File.ReadAllTextAsync(path));
            settings.DocumentPath = Path.Combine(folder, "absent.json"); await settings.LoadCommand.ExecuteAsync(null); Assert.Equal("bad JSON", settings.DocumentText);
            settings.DocumentPath = path; await settings.LoadCommand.ExecuteAsync(null); Assert.True(settings.IsValidated); Assert.False(settings.HasError); Assert.False(settings.IsDirty); Assert.Equal(initial.Text, settings.DocumentText);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ExportingAnotherConfigurationDoesNotClaimThatStartupWillUseIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "device-ui-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var b = new Bench(); await using var owner = b.Vm; var workspace = new DeviceConfigurationWorkspace(new JsonDeviceConfigurationDocument()); var path = Path.Combine(folder, "devices.json"); var active = workspace.Inspect(b.Runtime.Configuration);
            await using var settings = new DeviceSettingsViewModel(workspace, active, path); await settings.SaveCommand.ExecuteAsync(null);
            var changed = workspace.SelectPrimary(b.Runtime.Configuration, null); settings.DocumentPath = Path.Combine(folder, "export.json"); settings.DocumentText = changed.Text; await settings.SaveCommand.ExecuteAsync(null);
            Assert.False(settings.RestartRequired); Assert.Contains("已导出", settings.Message); Assert.Equal(active.Text, await File.ReadAllTextAsync(path));
            settings.DocumentPath = path; await settings.SaveCommand.ExecuteAsync(null); Assert.True(settings.RestartRequired);
            settings.DocumentText = active.Text; await settings.SaveCommand.ExecuteAsync(null); Assert.False(settings.RestartRequired);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { } }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public ConfiguredDeviceRuntime Runtime { get; }
        public MeasurementRunService Runs { get; }
        public MainWindowViewModel Vm { get; }
        public Bench(bool source = true)
        {
            var field = new DeviceConfiguration(new("magnet"), "SIM-FIELD", "Named magnet", ConfiguredDeviceCategory.Field, new SimulationDeviceConnection());
            var smu = new DeviceConfiguration(new("smu"), "K2450", "Named source", ConfiguredDeviceCategory.SourceMeter, new SimulationDeviceConnection());
            var config = new DeviceConfigurationCatalog(source ? smu.Id : null, source ? [field, smu] : [field], new Dictionary<string, InstrumentId> { ["field"] = field.Id });
            Runtime = new(config, new ConfiguredDeviceFactory(), Leases, Hub); Runs = source ? new(Runtime.PrimarySource, Leases, Hub, deviceRegistry: Runtime.Registry) : new(Runtime.Registry, Hub);
            var definition = Runtime.Registry.GetAxisDefinition(field.Id);
            var plan = new ExperimentPlan("Controller plan", new SequenceStep(new("root"), [new ReadStep(new("read"), [new(new("actual"), new(field.Id, definition.Readback.Id))]), new DelayStep(new("delay"), TimeSpan.FromSeconds(30))]));
            Vm = new(source ? Runtime.PrimarySource : null, Runs, Hub, new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), Runs), plan, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"), registry: Runtime.Registry);
        }
    }
}

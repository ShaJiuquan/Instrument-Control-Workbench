using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanTemperatureScanIntegrationTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static Quantity K(double value) => new(value, PhysicalUnit.Kelvin);
    private static string Palette(bool hardware) => hardware ? "硬件温度扫描" : "软件温度扫描";
    private static TemperatureScanStep Scan(ThermalPort port, bool hardware, ExperimentStep? customBody = null)
    {
        var body = new SequenceStep(new("scan-body"), [new ReadStep(new("temperature-read"), [new(new("measured"), new(port.Definition.Device.Id, port.Definition.Readback.Id))]), new DelayStep(new("settle-child"), TimeSpan.FromMilliseconds(10))]);
        var selectedBody = customBody ?? body;
        return hardware ? new HardwareTemperatureScanStep(new("scan"), port.Definition.Device.Id, new("thermal"), new([K(4), K(4.01), K(4.02)]), new(20, PhysicalUnit.KelvinPerMinute), true, K(.0005), 1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50), TemperatureRampSampling.Threshold, TimeSpan.FromMilliseconds(20), selectedBody)
            : new SoftwareTemperatureScanStep(new("scan"), port.Definition.Device.Id, new("thermal"), new([K(4), K(4.01), K(4.02)]), new(20, PhysicalUnit.KelvinPerMinute), true, K(.0005), 1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50), TemperatureRampSampling.Threshold, TimeSpan.FromMilliseconds(20), selectedBody);
    }
    private static void SameNode(ExperimentStep expected, ExperimentStep actual)
    {
        var codec = new JsonExperimentPlanDocument();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("same", expected))), JsonNode.Parse(codec.Serialize(new("same", actual)))));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingNodesRoundTripWithoutChangingIdentityBodyOrSettingsAndWithoutIo(bool hardware)
    {
        var b = new Bench(hardware); await using var owner = b.Shell; var original = Scan(b.Port, hardware); var callbacks = 0;
        var form = new PlanStepFormViewModel(original, b.Workspace.Devices, () => callbacks++, temperatureScanAxes: b.Workspace.TemperatureScanDefinitions);
        Assert.True(form.HasTemperatureScan); Assert.False(form.IsDirty); Assert.Equal(0, callbacks); SameNode(original, form.Build());
        var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id == original.Id).SelectCommand.Execute(null);
        Assert.True(vm.SupportsNodeForm); Assert.False(vm.NodeFragmentDirty); SameNode(original, vm.NodeForm!.Build()); Assert.Equal(0, b.Port.IoCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaletteRequiresAnExplicitControllerAndAppendsOnlyAfterValidationWithoutIo(bool hardware)
    {
        var b = new Bench(hardware, withScan: false); await using var owner = b.Shell; var vm = owner.Plans!; Assert.Contains(Palette(hardware), vm.PaletteKinds); vm.PaletteKind = Palette(hardware);
        var form = vm.PaletteForm!.TemperatureScan!; Assert.Null(form.SelectedController); Assert.Empty(form.Column); Assert.Empty(form.ValuesForm.Definition.ValuesText);
        var before = vm.DocumentText; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        Fill(form); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(vm.IsValidated);
        var step = Assert.IsAssignableFrom<TemperatureScanStep>(Assert.Single(Assert.IsType<SequenceStep>(new JsonExperimentPlanDocument().Parse(vm.DocumentText).Root).Children));
        Assert.Equal(hardware, step is HardwareTemperatureScanStep); Assert.Equal(b.Port.Definition.Device.Id, step.Device); Assert.Equal(0, b.Port.IoCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphDraftApplyDiscardBusyAndStaleCallbacksProtectTheValidatedRecipe(bool hardware)
    {
        var b = new Bench(hardware); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null); var before = vm.DocumentText;
        var stale = vm.NodeForm!.TemperatureScan!; stale.Rate = "12"; Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        vm.DiscardNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.False(vm.NodeFragmentDirty); stale.Rate = "11"; Assert.False(vm.NodeFragmentDirty);
        vm.NodeForm!.TemperatureScan!.Rate = "0"; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        var appliedForm = vm.NodeForm!.TemperatureScan!; appliedForm.Rate = "15"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False(vm.NodeFragmentDirty); var applied = vm.DocumentText;
        var edited = Assert.IsAssignableFrom<TemperatureScanStep>(Assert.Single(Assert.IsType<SequenceStep>(new JsonExperimentPlanDocument().Parse(applied).Root).Children));
        Assert.Equal(Scan(b.Port, hardware).Id, edited.Id); SameNode(Scan(b.Port, hardware).Body, edited.Body); Assert.Equal(new(15, PhysicalUnit.KelvinPerMinute), edited.Rate);
        appliedForm.Rate = "14"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.NodeForm!.TemperatureScan!.IsReadOnly); Assert.True(vm.NodeForm.TemperatureScan.ValuesForm.IsReadOnly); Assert.False(vm.ApplyNodeCommand.CanExecute(null)); Assert.False(vm.AppendNodeCommand.CanExecute(null));
        vm.NodeForm.TemperatureScan.Rate = "13"; vm.ApplyNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText);
        owner.IsBusy = false; vm.RefreshCommands(); vm.DiscardNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText); Assert.False(vm.NodeFragmentDirty); Assert.Equal(0, b.Port.IoCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormAppliedRecipeSavesReloadsAndProducesRealThreePointArchiveAndCsv(bool hardware)
    {
        var folder = Path.Combine(Path.GetTempPath(), "temperature-form-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var author = new Bench(hardware); await using var authorShell = author.Shell; var vm = authorShell.Plans!;
            vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null); vm.NodeForm!.TemperatureScan!.Column = "recorded-temperature"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Equal(0, author.Port.IoCalls);
            var path = Path.Combine(folder, "plan.json"); await author.Workspace.SaveAsync(path, vm.DocumentText, Token); var loaded = await author.Workspace.LoadAsync(path, Token); Assert.Equal(0, author.Port.IoCalls);
            var scan = Assert.IsAssignableFrom<TemperatureScanStep>(Assert.Single(Assert.IsType<SequenceStep>(loaded.Plan.Root).Children));
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var replay = new Bench(hardware, listener: archive); await using var replayShell = replay.Shell;
            await replay.Runs.StartPlanAsync(loaded.Plan, Token); var data = replay.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count); Assert.False(replay.Runs.HasActiveRun); Assert.True(replay.Port.IoCalls > 0);
            Assert.Equal(new[] { 0d, 1d, 2d }, data.Rows.Select(row => row.Values[scan.IndexColumn].Value)); Assert.All(data.Rows, row => { Assert.Equal(3, row.Values[scan.TotalColumn].Value); Assert.Equal(PhysicalUnit.Kelvin, row.Values[scan.Column].Unit); Assert.True(row.Values[scan.Column].Value >= scan.Points.Values[(int)row.Values[scan.IndexColumn].Value].Value); });
            Assert.Equal(new TemperatureControlConfiguration(false, true), await replay.Port.ReadControlConfigurationAsync(Token));
            if (scan is SoftwareTemperatureScanStep software) Assert.Equal(K(4.02), data.Rows[^1].Values[software.SetpointColumn]);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var restored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.Count, restored.DataSet.Rows.Count); SameNode(loaded.Plan.Root, restored.DataSet.Plan!.Root);
            for (var index = 0; index < data.Rows.Count; index++) Assert.Equal(data.Rows[index].Values, restored.DataSet.Rows[index].Values);
            Assert.NotNull(restored.DataSet.Blackboard); await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "data.csv"));
            Assert.Contains("recorded-temperature [Kelvin]", csv); Assert.Contains("recorded-temperature.index [Dimensionless]", csv); Assert.Contains("recorded-temperature.total [Dimensionless]", csv);
            if (!hardware) Assert.Contains("recorded-temperature.setpoint [Kelvin]", csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static void Fill(PlanTemperatureScanFormViewModel form)
    {
        form.SelectedController = Assert.Single(form.Controllers); form.Column = "thermal"; form.ValuesForm.Definition.ValuesText = "4, 4.01, 4.02"; form.Rate = "20"; form.WaitStableFirst = true; form.Tolerance = ".0005"; form.StableCount = "1"; form.StableTimeoutSeconds = "2"; form.StepTimeoutSeconds = "2"; form.PollSeconds = ".05"; form.Sampling = TemperatureRampSampling.Threshold; form.IntervalSeconds = ".02";
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamingTheScanAxisCannotSilentlyBreakChildVariableScope(bool hardware)
    {
        var b = new Bench(hardware, axisDependentBody: true); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null); var before = vm.DocumentText;
        vm.NodeForm!.TemperatureScan!.Column = "renamed"; vm.ApplyNodeCommand.Execute(null);
        Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.Equal(0, b.Port.IoCalls);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.NodeFragmentDirty); Assert.Equal(before, vm.DocumentText);
    }
    [Fact]
    public void ControllerCatalogUsesConfigurationCapabilityBeforeAnyConnection()
    {
        var port = new ThermalPort(); var axisOnly = new LegacyTemperaturePort(); var hub = new NotificationHub();
        var registry = new DeviceRegistry([new AxisControllerSession(port, hub), new AxisControllerSession(axisOnly, hub), new AxisControllerSession(MockEnvironmentCatalog.Field(), hub)], new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
        Assert.Equal(3, workspace.AxisDefinitions.Count); Assert.Equal(port.Definition, Assert.Single(workspace.TemperatureScanDefinitions));
        foreach (var hardware in new[] { false, true })
        {
            var form = new PlanStepFormViewModel(Scan(port, hardware), workspace.Devices, () => { }, template: true, temperatureScanAxes: workspace.TemperatureScanDefinitions);
            Assert.Equal(port.Definition, Assert.Single(form.TemperatureScan!.Controllers).Definition); Assert.Null(form.TemperatureScan.SelectedController);
        }
        Assert.Equal(0, port.IoCalls); Assert.Equal(0, axisOnly.Inner.IoCalls);
    }
    private sealed class Bench
    {
        public ThermalPort Port { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench(bool hardware, bool withScan = true, IRunResultListener? listener = null, bool axisDependentBody = false)
        {
            var hub = new NotificationHub(); var registry = new DeviceRegistry([new AxisControllerSession(Port, hub)], new(TimeSpan.FromSeconds(5))); Runs = new(registry, hub, listener is null ? null : [listener]); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            ExperimentStep? body = axisDependentBody ? new AssignValueStep(new("remember-axis"), new("remembered"), new VariableValue(new("thermal"))) : null;
            var plan = new ExperimentPlan("Temperature authoring", new SequenceStep(new("root"), withScan ? [Scan(Port, hardware, body)] : [])); Shell = new(null, Runs, hub, Workspace, plan, "unused-temperature.json");
        }
    }
    private sealed class ThermalPort : ITemperatureConfigurationPort
    {
        private readonly MockAxisController inner;
        public ThermalPort(string id = "temperature") => inner = MockEnvironmentCatalog.PassiveTemperature(id: new(id));
        public int IoCalls { get; private set; }
        public AxisControllerDefinition Definition => inner.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) { IoCalls++; return inner.ConnectAsync(token); }
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) { IoCalls++; return inner.GetSnapshotAsync(token); }
        public Task SetTargetAsync(Quantity value, CancellationToken token) { IoCalls++; return inner.SetTargetAsync(value, token); }
        public Task SetRateAsync(Quantity value, CancellationToken token) { IoCalls++; return inner.SetRateAsync(value, token); }
        public Task HoldAsync(CancellationToken token) { IoCalls++; return inner.HoldAsync(token); }
        public Task DisconnectAsync(CancellationToken token) { IoCalls++; return inner.DisconnectAsync(token); }
        public Task PrepareSoftwareRampAsync(Quantity value, CancellationToken token) { IoCalls++; return inner.PrepareSoftwareRampAsync(value, token); }
        public Task SetHardwareRampAsync(Quantity value, CancellationToken token) { IoCalls++; return inner.SetHardwareRampAsync(value, token); }
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token) { IoCalls++; return inner.ReadControlConfigurationAsync(token); }
        public Task ConfigureControlAsync(TemperatureControlConfiguration configuration, Quantity? rate, CancellationToken token) { IoCalls++; return inner.ConfigureControlAsync(configuration, rate, token); }
    }
    private sealed class LegacyTemperaturePort : ITemperatureControlPort
    {
        public ThermalPort Inner { get; } = new("legacy-kelvin");
        public AxisControllerDefinition Definition => Inner.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => Inner.ConnectAsync(token);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => Inner.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity value, CancellationToken token) => Inner.SetTargetAsync(value, token);
        public Task SetRateAsync(Quantity value, CancellationToken token) => Inner.SetRateAsync(value, token);
        public Task HoldAsync(CancellationToken token) => Inner.HoldAsync(token);
        public Task DisconnectAsync(CancellationToken token) => Inner.DisconnectAsync(token);
        public Task PrepareSoftwareRampAsync(Quantity value, CancellationToken token) => Inner.PrepareSoftwareRampAsync(value, token);
        public Task SetHardwareRampAsync(Quantity value, CancellationToken token) => Inner.SetHardwareRampAsync(value, token);
        public Task<TemperatureControlConfiguration> ReadControlConfigurationAsync(CancellationToken token) => Inner.ReadControlConfigurationAsync(token);
    }
}

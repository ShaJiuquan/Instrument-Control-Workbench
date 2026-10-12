using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanSr830FormTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static DeviceDescriptor ReferenceDevice() => new(new("lockin"), "SR830", "Reference-capable simulator", DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Sr830Parameters.WithReference);
    private static DeviceDescriptor InputDevice() => new(new("input-only"), "SR830", "Input-only simulator", DriverKind.Simulation, DeviceStopPolicy.AbortAcquisition, Sr830Parameters.All);
    [Theory]
    [InlineData(false, Sr830InputMode.VoltageAB)]
    [InlineData(true, Sr830InputMode.VoltageA)]
    [InlineData(true, Sr830InputMode.Current1M)]
    [InlineData(true, Sr830InputMode.Current100M)]
    public void ExistingFrameRoundTripsWithoutDirtyingOrReplacingAnyField(bool reference, Sr830InputMode input)
    {
        var original = new ReadSr830FrameStep(new("read"), new("lockin"), input, new("raw"), reference); var changes = 0;
        var form = new PlanStepFormViewModel(original, [ReferenceDevice(), InputDevice()], () => changes++); Assert.True(form.IsSupported); Assert.True(form.HasSr830Frame); Assert.False(form.HasAcquisition); Assert.False(form.IsDirty); Assert.Equal(0, changes);
        var built = Assert.IsType<ReadSr830FrameStep>(form.Build()); Assert.Equal(original.Device, built.Device); Assert.Equal(input, built.Input); Assert.Equal(original.Prefix, built.Prefix); Assert.Equal(reference, built.IncludeReference); Assert.Equal(original.Id, built.Id);
        form.Sr830Frame!.Prefix = "next"; Assert.True(form.IsDirty); Assert.True(changes > 0); Assert.Equal("next", ((ReadSr830FrameStep)form.Build()).Prefix.Value);
    }
    [Fact]
    public void TemplatesRequireExplicitDeviceInputAndPrefixSelections()
    {
        var frame = new ReadSr830FrameStep(new("read"), new("unselected"), Sr830InputMode.Current1M, new("raw")); var form = new PlanSr830FrameFormViewModel(frame, [ReferenceDevice()], () => { }, true);
        Assert.Null(form.SelectedDevice); Assert.Null(form.SelectedInput); Assert.Empty(form.Prefix); Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedDevice = Assert.Single(form.Devices); Assert.Throws<InvalidOperationException>(() => form.Build()); form.SelectedInput = Sr830InputMode.Current1M; Assert.Throws<ArgumentException>(() => form.Build()); form.Prefix = "authored"; Assert.Equal("authored", form.Build().Prefix.Value);
    }
    [Fact]
    public void UnsupportedDeviceSwitchPreservesReferenceOptionUntilExplicitlyCleared()
    {
        var form = new PlanSr830FrameFormViewModel(new(new("read"), new("lockin"), Sr830InputMode.VoltageAB, new("raw"), true), [ReferenceDevice(), InputDevice()], () => { });
        form.SelectedDevice = form.Devices.Single(d => d.Descriptor.Id.Value == "input-only"); Assert.True(form.IncludeReference); Assert.False(form.ReferenceAvailable); Assert.True(form.CanChooseReference); Assert.Throws<InvalidOperationException>(() => form.Build());
        form.IncludeReference = false; Assert.False(form.CanChooseReference); Assert.False(form.Build().IncludeReference);
    }
    [Theory]
    [InlineData("model")]
    [InlineData("policy")]
    [InlineData("parameters")]
    public void ChoicesExcludeProfilesThatCannotProvideTheFrameContract(string damage)
    {
        var valid = ReferenceDevice(); var invalid = new DeviceDescriptor(new("bad"), damage == "model" ? "K2182" : "SR830", "Invalid profile", DriverKind.Simulation, damage == "policy" ? DeviceStopPolicy.OutputOff : DeviceStopPolicy.AbortAcquisition, damage == "parameters" ? Sr830Parameters.All.Take(4) : Sr830Parameters.WithReference);
        var form = new PlanSr830FrameFormViewModel(new(new("read"), valid.Id, Sr830InputMode.VoltageAB, new("raw")), [valid, invalid], () => { }); Assert.Single(form.Devices);
        form.SelectedDevice = new(invalid); Assert.Throws<InvalidOperationException>(() => form.Build());
    }
    [Theory]
    [InlineData("")]
    [InlineData("bad prefix")]
    [InlineData("bad/column")]
    public void InvalidPrefixCannotCreateNativeColumns(string prefix)
    {
        var form = new PlanSr830FrameFormViewModel(new(new("read"), new("lockin"), Sr830InputMode.VoltageAB, new("raw")), [ReferenceDevice()], () => { }); form.Prefix = prefix; Assert.Throws<ArgumentException>(() => form.Build());
    }
    [Fact]
    public void LoadedCustomCalculationIsPreservedUntilExplicitGeneration()
    {
        var frame = new ReadSr830FrameStep(new("read"), new("lockin"), Sr830InputMode.Current1M, new("raw"), true); var custom = new CalculatedMeasurementStep(new("custom"), frame, [new(new("custom_result"), new ConstantValue(new(9, PhysicalUnit.Dimensionless)))], true);
        var outer = new PlanStepFormViewModel(custom, [ReferenceDevice()], () => { }); var form = outer.CalculationForm!; Assert.True(form.CanEditBody); Assert.True(form.HasSr830Body); Assert.False(outer.IsDirty);
        var codec = new JsonExperimentPlanDocument(); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("same", custom))), JsonNode.Parse(codec.Serialize(new("same", form.Build())))));
        form.NominalResultPrefix = "signal"; Assert.False(outer.IsDirty); Assert.True(form.GenerateSr830NominalCommand.CanExecute(null)); form.GenerateSr830NominalCommand.Execute(null);
        Assert.True(outer.IsDirty); var generated = form.Build(); Assert.Equal(5, generated.Columns.Count); Assert.True(generated.ContinueOnUnavailable); Assert.DoesNotContain(generated.Columns, c => c.Column.Value == "custom_result");
        form.BodyForm.Sr830Frame!.SelectedInput = Sr830InputMode.Current100M; Assert.Contains("变化", form.NominalGenerationMessage); form.GenerateSr830NominalCommand.Execute(null); var gain = Assert.Single(form.Build().Columns, c => c.Column.Value.EndsWith("nominal_transimpedance_ohm", StringComparison.Ordinal)); Assert.Equal(1e8, Assert.IsType<ConstantValue>(gain.Expression).Value.Value);
        form.BodyForm.Sr830Frame.Prefix = "renamed"; form.GenerateSr830NominalCommand.Execute(null); Assert.Contains(new ParameterId("renamed.x_v"), NumericPlanValue.Variables(form.Build().Columns[0].Expression));
    }
    [Fact]
    public void ReadOnlyCalculationDisablesGeneratorAndNestedFrameControls()
    {
        var frame = new ReadSr830FrameStep(new("read"), new("lockin"), Sr830InputMode.Current1M, new("raw"), true); var step = Sr830InputReferredAnalysis.Create(new("refer"), frame, new("signal")); var outer = new PlanStepFormViewModel(step, [ReferenceDevice()], () => { }); var form = outer.CalculationForm!;
        form.NominalResultPrefix = "next"; Assert.True(form.GenerateSr830NominalCommand.CanExecute(null)); outer.IsReadOnly = true; Assert.True(form.BodyForm.Sr830Frame!.IsReadOnly); Assert.False(form.BodyForm.Sr830Frame.CanChooseReference); Assert.False(form.GenerateSr830NominalCommand.CanExecute(null));
        form.GenerateSr830NominalCommand.Execute(null); Assert.Equal("signal.input_x", form.Columns[0].ColumnId); Assert.False(outer.IsDirty); outer.IsReadOnly = false; Assert.True(form.GenerateSr830NominalCommand.CanExecute(null));
    }
    [Theory]
    [InlineData("SR830 原始帧")]
    [InlineData("SR830 名义换算")]
    public async Task PaletteCompletesThroughApplicationPreflightWithoutConnectingAnyDevice(string kind)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; Assert.Contains(kind, vm.PaletteKinds); vm.PaletteKind = kind; var palette = Assert.IsType<PlanStepFormViewModel>(vm.PaletteForm); var before = vm.DocumentText;
        vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); await b.AssertDisconnected();
        var frame = kind == "SR830 原始帧" ? palette.Sr830Frame! : palette.CalculationForm!.BodyForm.Sr830Frame!;
        frame.SelectedDevice = Assert.Single(frame.Devices); frame.SelectedInput = Sr830InputMode.Current1M; frame.Prefix = "authored"; frame.IncludeReference = true;
        if (kind == "SR830 名义换算") { var form = palette.CalculationForm!; form.NominalResultPrefix = "signal"; Assert.True(form.GenerateSr830NominalCommand.CanExecute(null)); form.GenerateSr830NominalCommand.Execute(null); Assert.Contains("Ampere", form.Columns[0].PreviewSummary); Assert.False(form.HasPreviewMessage, form.PreviewMessage); }
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(vm.IsValidated); await b.AssertDisconnected();
        if (kind == "SR830 原始帧") { var node = vm.GraphNodes.Single(n => n.Id != new StepId("root") && n.Id != new StepId("input")); Assert.Equal("SR830 原始帧", node.Title); node.SelectCommand.Execute(null); Assert.True(vm.SupportsNodeForm); }
    }
    [Fact]
    public async Task InvalidNodeDraftPreservesDocumentAndBlocksRunUntilDiscard()
    {
        var b = new Bench(withFrame: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "read").SelectCommand.Execute(null); var before = vm.DocumentText;
        vm.NodeForm!.Sr830Frame!.Prefix = ""; Assert.True(vm.NodeFragmentDirty); Assert.False(vm.IsValidated); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null)); vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.NodeFragmentDirty); Assert.Equal("raw", vm.NodeForm!.Sr830Frame!.Prefix); Assert.False(vm.HasError); await b.AssertDisconnected();
    }
    [Fact]
    public async Task BusyWorkspaceKeepsSr830PaletteReadOnlyAndCannotAppendGeneratedDraft()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "SR830 名义换算"; var form = vm.PaletteForm!.CalculationForm!;
        var frame = form.BodyForm.Sr830Frame!; frame.SelectedDevice = Assert.Single(frame.Devices); frame.SelectedInput = Sr830InputMode.Current1M; frame.Prefix = "raw"; form.NominalResultPrefix = "signal"; Assert.True(form.GenerateSr830NominalCommand.CanExecute(null));
        var before = vm.DocumentText; owner.IsBusy = true; vm.RefreshCommands(); Assert.True(frame.IsReadOnly); Assert.False(form.GenerateSr830NominalCommand.CanExecute(null)); Assert.False(vm.AppendNodeCommand.CanExecute(null));
        form.GenerateSr830NominalCommand.Execute(null); vm.AppendNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); await b.AssertDisconnected(); owner.IsBusy = false; vm.RefreshCommands(); Assert.True(form.GenerateSr830NominalCommand.CanExecute(null));
    }
    [Fact]
    public async Task FormAuthoredNominalAnalysisSavesReloadsRunsAndExportsActualReferenceEvidence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sr830-form-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "SR830 名义换算"; var form = vm.PaletteForm!.CalculationForm!; var frame = form.BodyForm.Sr830Frame!;
            frame.SelectedDevice = Assert.Single(frame.Devices); frame.SelectedInput = Sr830InputMode.Current1M; frame.Prefix = "ui_raw"; frame.IncludeReference = true; form.NominalResultPrefix = "ui_signal"; form.GenerateSr830NominalCommand.Execute(null); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
            var path = Path.Combine(folder, "authored.json"); await b.Workspace.SaveAsync(path, vm.DocumentText, Token); var loaded = await b.Workspace.LoadAsync(path, Token);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var runtime = new Bench(listener: archive); await runtime.Runs.StartPlanAsync(loaded.Plan, Token); var data = runtime.Runs.LatestDataSet!; Assert.Equal(2, data.Rows.Count); Assert.Equal(new(1e-9, PhysicalUnit.Ampere), data.Rows[1].Values[new("ui_signal.input_x")]); Assert.Equal(new(.004, PhysicalUnit.Volt), data.Rows[1].Values[new("ui_raw.sine_amplitude_v")]); Assert.Equal(1e6, data.Rows[1].Values[new("ui_signal.nominal_transimpedance_ohm")].Value); Assert.False(runtime.Runs.HasActiveRun); await runtime.Shell.DisposeAsync();
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows[1].Values[new("ui_signal.input_x")], stored.DataSet.Rows[1].Values[new("ui_signal.input_x")]); await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "export.csv"), Token); Assert.Contains("ui_signal.input_x [Ampere]", await File.ReadAllTextAsync(Path.Combine(folder, "export.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new(); public MockSr830Meter Meter { get; } = new(new("lockin"), "Explicit lock-in simulator");
        public MainWindowViewModel Shell { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench(bool withFrame = false, IRunResultListener? listener = null)
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); var registry = new DeviceRegistry([manager, new Sr830MeterSession(Meter, hub)], new(TimeSpan.FromSeconds(5)));
            Runs = new(registry, hub, listener is null ? null : [listener]); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var input = new SetParameterStep(new("input"), new(Meter.Descriptor.Id, Sr830Parameters.InputMode.Id), new ConstantValue(new(2, PhysicalUnit.Dimensionless)));
            var plan = new ExperimentPlan("SR830 form fixture", new SequenceStep(new("root"), withFrame ? [input, new ReadSr830FrameStep(new("read"), Meter.Descriptor.Id, Sr830InputMode.Current1M, new("raw"), true)] : [input]));
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-sr830.json");
        }
        public async Task AssertDisconnected() { Assert.False((await Source.GetSnapshotAsync(Token)).Connected); Assert.False((await Meter.GetSnapshotAsync(Token)).Connected); }
    }
}

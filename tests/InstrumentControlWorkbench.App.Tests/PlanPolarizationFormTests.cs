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

public sealed class PlanPolarizationFormTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static ReadPolarizationStep Pulse(InstrumentId device, bool dynamic = false, MatrixRouteExpectation? route = null) => new(new("pulse"), device, new(dynamic ? 1 : 5, PhysicalUnit.Volt), TimeSpan.Zero, new(0, PhysicalUnit.Volt), TimeSpan.Zero, new(1, PhysicalUnit.Volt), TimeSpan.Zero, new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 2, false, new(.001, PhysicalUnit.Ampere), new(true, false, true, 3, AveragingMode.Moving)), new("v"), new("i"), new("attempts"), 8, route, requestedPolarizationColumn: dynamic ? new("requested") : null, polarizationTarget: dynamic ? new VariableValue(new("pol_axis")) : null);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedAndDynamicRecipesRoundTripWithoutDirtyingOrLosingFields(bool dynamic)
    {
        var source = new MockKeithley2450SourceMeter(); var original = Pulse(source.Id, dynamic, new(new("matrix"), [new(1, 1, 1)])); var changes = 0;
        var outer = new PlanStepFormViewModel(original, [source.Descriptor], () => changes++); Assert.True(outer.IsSupported); Assert.True(outer.HasPolarization); Assert.True(outer.HasEnvironment); Assert.False(outer.IsDirty); Assert.Equal(0, changes);
        var codec = new JsonExperimentPlanDocument(); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("same", original))), JsonNode.Parse(codec.Serialize(new("same", outer.Build())))));
        outer.Polarization!.ReadVoltage = "2"; var edited = Assert.IsType<ReadPolarizationStep>(outer.Build()); Assert.Equal(original.PolarizationTarget, edited.PolarizationTarget); Assert.Equal(original.RequiredRoute, edited.RequiredRoute); Assert.Equal(original.Sense.Acquisition, edited.Sense.Acquisition); Assert.Equal(original.RequestedPolarizationColumn, edited.RequestedPolarizationColumn); Assert.True(outer.IsDirty);
    }
    [Fact]
    public void TemplatesRequireExplicitSourceModeVoltageComplianceAndColumns()
    {
        var source = new MockKeithley2450SourceMeter(); var outer = new PlanStepFormViewModel(Pulse(source.Id), [source.Descriptor], () => { }, template: true); var form = outer.Polarization!;
        Assert.Null(form.SelectedSource); Assert.Null(form.SelectedMode); Assert.Empty(form.Target.NumberText); Assert.Empty(form.ReadVoltage); Assert.Empty(form.Compliance); Assert.Empty(form.CurrentColumn); Assert.Throws<InvalidOperationException>(() => form.Build());
        Fill(form); Assert.Equal(source.Id, form.Build().Device); Assert.Equal(new(5, PhysicalUnit.Volt), form.Build().PolarizationVoltage);
    }
    [Theory]
    [InlineData("duration", "-1")]
    [InlineData("voltage", "211")]
    [InlineData("read", "NaN")]
    [InlineData("compliance", "0")]
    [InlineData("range", ".0009")]
    [InlineData("nplc", "11")]
    [InlineData("attempts", "1.5")]
    [InlineData("attempts", "101")]
    [InlineData("column", "v")]
    public void InvalidControlsFailBeforeProducingAStep(string field, string value)
    {
        var source = new MockKeithley2450SourceMeter(); var form = new PlanStepFormViewModel(Pulse(source.Id), [source.Descriptor], () => { }).Polarization!;
        switch (field) { case "duration": form.PolarizationSeconds = value; break; case "voltage": form.Target.NumberText = value; break; case "read": form.ReadVoltage = value; break; case "compliance": form.Compliance = value; break; case "range": form.ManualRange = value; break; case "nplc": form.Nplc = value; break; case "attempts": form.MaximumAttempts = value; break; case "column": form.CurrentColumn = value; break; }
        Assert.ThrowsAny<Exception>(() => form.Build());
    }
    [Fact]
    public void ChangingSourceCannotSilentlyReassignFrozenMatrixConnections()
    {
        var first = new MockKeithley2450SourceMeter(new("first"), "First"); var second = new MockKeithley2450SourceMeter(new("second"), "Second");
        var form = new PlanStepFormViewModel(Pulse(first.Id, route: new(new("matrix"), [new(1, 1, 1)])), [first.Descriptor, second.Descriptor], () => { }).Polarization!;
        form.SelectedSource = form.Sources.Single(s => s.Descriptor.Id == second.Id); Assert.Throws<InvalidOperationException>(() => form.Build());
    }
    [Fact]
    public void DynamicTargetCannotLoseItsRequiredRequestedVoltageColumn()
    {
        var source = new MockKeithley2450SourceMeter(); var form = new PlanStepFormViewModel(Pulse(source.Id, true), [source.Descriptor], () => { }).Polarization!;
        form.RecordRequestedVoltage = false; Assert.Throws<ArgumentException>(() => form.Build()); Assert.Equal(PlanValueInputKind.Variable, form.Target.Kind);
    }
    [Fact]
    public void ReadOnlyPropagatesToTargetEnvironmentAndDependentControlsWithoutDirtying()
    {
        var source = new MockKeithley2450SourceMeter(); var outer = new PlanStepFormViewModel(Pulse(source.Id, true), [source.Descriptor], () => { }); var form = outer.Polarization!;
        Assert.True(form.CanEditRange); Assert.True(form.CanEditRequestedColumn); outer.IsReadOnly = true; Assert.True(form.IsReadOnly); Assert.True(form.Target.IsReadOnly); Assert.True(form.Environment.IsReadOnly); Assert.False(form.CanEditRange); Assert.False(form.CanEditRequestedColumn); Assert.False(outer.IsDirty);
        outer.IsReadOnly = false; Assert.True(form.CanEditRange); Assert.True(form.CanEditRequestedColumn); Assert.False(outer.IsDirty);
    }
    [Fact]
    public void CalculationBodyKeepsItsCustomFormulaWhenPulseSettingsChange()
    {
        var source = new MockKeithley2450SourceMeter(); var pulse = Pulse(source.Id); var expression = new ArithmeticValue(ArithmeticOperator.Divide, new VariableValue(pulse.VoltageColumn), new VariableValue(pulse.CurrentColumn));
        var calc = new CalculatedMeasurementStep(new("calculate"), pulse, [new(new("resistance"), expression)], true); var outer = new PlanStepFormViewModel(calc, [source.Descriptor], () => { });
        Assert.True(outer.CalculationForm!.CanEditBody); var form = outer.CalculationForm.BodyForm.Polarization!; form.ReadVoltage = "2";
        var built = Assert.IsType<CalculatedMeasurementStep>(outer.Build()); Assert.Equal(expression, Assert.Single(built.Columns).Expression); Assert.True(built.ContinueOnUnavailable); Assert.Equal(new(2, PhysicalUnit.Volt), Assert.IsType<ReadPolarizationStep>(built.Body).ReadVoltage);
    }
    [Fact]
    public async Task VoltageIteratorPaletteCreatesScopedDynamicPulseAndSurvivesSaveRunArchiveCsv()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pulse-form-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = "极化电压迭代";
            var iterator = vm.PaletteForm!.IterationForm!; Assert.Equal(PhysicalUnit.Volt, iterator.Unit); iterator.Variable = "pol_axis"; iterator.ValuesForm.Definition.ValuesText = "5, 0, -5"; vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); await b.AssertDisconnected();
            var codec = new JsonExperimentPlanDocument(); var loop = Assert.IsType<ForEachStep>(Assert.Single(Assert.IsType<SequenceStep>(codec.Parse(vm.DocumentText).Root).Children));
            vm.GraphNodes.Single(n => n.Id == loop.Body.Id).SelectCommand.Execute(null); vm.PaletteKind = "极化脉冲读出"; var pulse = vm.PaletteForm!.Polarization!; Fill(pulse); pulse.Target.Kind = PlanValueInputKind.Variable;
            pulse.Target.VariablePicker.SelectedChoice = Assert.Single(pulse.Target.VariablePicker.Choices, choice => choice.Id == "pol_axis"); Assert.Equal("pol_axis", pulse.Target.Variable);
            vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); await b.AssertDisconnected();
            var path = Path.Combine(folder, "plan.json"); await b.Workspace.SaveAsync(path, vm.DocumentText, Token); var loaded = await b.Workspace.LoadAsync(path, Token);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var replay = new Bench(listener: archive); await replay.Runs.StartPlanAsync(loaded.Plan, Token); var data = replay.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count);
            Assert.Equal(new[] { 5.0, 0, -5 }, data.Rows.Select(row => row.Values[new("requested")].Value)); Assert.All(data.Rows, row => Assert.Equal(new(1, PhysicalUnit.Volt), row.Values[new("v")])); Assert.False((await replay.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(replay.Runs.HasActiveRun); await replay.Shell.DisposeAsync();
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var restored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.SelectMany(r => r.Quality), restored.DataSet.Rows.SelectMany(r => r.Quality));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); Assert.Contains("requested [Volt]", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static void Fill(PlanPolarizationFormViewModel form)
    {
        form.SelectedSource = Assert.Single(form.Sources); form.SelectedMode = SensingMode.TwoWire; form.Target.NumberText = "5"; form.PolarizationSeconds = "0"; form.InterPulseSeconds = "0"; form.ReadVoltage = "1"; form.ReadDelaySeconds = "0"; form.Compliance = ".001"; form.Nplc = "2";
        form.UseManualRange = true; form.ManualRange = ".001"; form.VoltageColumn = "v"; form.CurrentColumn = "i"; form.AttemptsColumn = "attempts"; form.RecordRequestedVoltage = true; form.RequestedVoltageColumn = "requested";
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new(); public MainWindowViewModel Shell { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench(bool withPulse = false, IRunResultListener? listener = null)
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); var registry = new DeviceRegistry([manager], new(TimeSpan.FromSeconds(5)));
            Runs = new(registry, hub, listener is null ? null : [listener]); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var plan = new ExperimentPlan("Pulse authoring", new SequenceStep(new("root"), withPulse ? [Pulse(Source.Id)] : []), [new(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-pulse.json");
        }
        public async Task AssertDisconnected() => Assert.False((await Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task PaletteValidatesAppendsAndRespectsBusyWithoutDeviceIo()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; Assert.Contains("极化脉冲读出", vm.PaletteKinds); vm.PaletteKind = "极化脉冲读出"; var before = vm.DocumentText;
        vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); await b.AssertDisconnected();
        var form = vm.PaletteForm!.Polarization!; Fill(form); owner.IsBusy = true; vm.RefreshCommands(); Assert.True(form.IsReadOnly); Assert.False(vm.AppendNodeCommand.CanExecute(null)); Assert.Equal(before, vm.DocumentText);
        owner.IsBusy = false; vm.RefreshCommands(); Assert.False(form.IsReadOnly); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(vm.IsValidated); Assert.Contains("read_polarization", vm.DocumentText); await b.AssertDisconnected();
    }
    [Fact]
    public async Task ExistingPulseApplyAndDiscardPreserveDocumentAndAcquisitionSettings()
    {
        var b = new Bench(withPulse: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "pulse").SelectCommand.Execute(null); var before = vm.DocumentText;
        var form = vm.NodeForm!.Polarization!; form.ReadVoltage = "2"; Assert.True(vm.NodeForm.IsDirty); vm.DiscardNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.False(vm.NodeForm!.IsDirty);
        vm.NodeForm.Polarization!.ReadVoltage = "2"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.NotEqual(before, vm.DocumentText); await b.AssertDisconnected();
    }
}

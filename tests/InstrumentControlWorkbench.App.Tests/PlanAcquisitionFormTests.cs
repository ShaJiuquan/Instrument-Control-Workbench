using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanAcquisitionFormTests
{
    [Theory]
    [InlineData("sourceVoltage")]
    [InlineData("sourceCurrent")]
    [InlineData("resistanceVoltage")]
    [InlineData("resistanceCurrent")]
    [InlineData("independentConstant")]
    [InlineData("independentVariable")]
    [InlineData("independentExpression")]
    [InlineData("sampleIntegrated")]
    [InlineData("sampleIndependent")]
    public void ExistingAcquisitionsRoundTripEveryFieldAndRetainEnvironmentRoutingAndExpressions(string kind)
    {
        var codec = new JsonExperimentPlanDocument(); var step = Step(kind); var calls = 0;
        var form = new PlanStepFormViewModel(step, Catalog(), () => calls++);
        Assert.True(form.IsSupported); Assert.True(form.HasAcquisition); Assert.False(form.IsDirty); Assert.Equal(0, calls);
        var built = form.Build();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("roundtrip", step))), JsonNode.Parse(codec.Serialize(new("roundtrip", built)))));
        if (step is ReadSampleResistanceStep sample) { Assert.Same(sample.Binding, ((ReadSampleResistanceStep)built).Binding); Assert.Contains("样品", form.Acquisition!.PreservedContext); }
        if (step is ReadCurrentResistanceStep original && original.ExpectedProgrammedCurrent is not ConstantValue)
        {
            form.Acquisition!.UseExpectedCurrent = false; form.Acquisition.ExpectedCurrent = "NaN";
            Assert.Same(original.ExpectedProgrammedCurrent, ((ReadCurrentResistanceStep)form.Build()).ExpectedProgrammedCurrent);
        }
        form.Acquisition!.VoltageColumn = "volts"; Assert.True(form.IsDirty); Assert.True(calls > 0);
        Assert.Equal("volts", built is ReadSourceSenseStep ? ((ReadSourceSenseStep)form.Build()).VoltageColumn.Value
            : (form.Build() is ReadSampleResistanceStep wrapped ? wrapped.Acquisition : (ResistanceAcquisitionStep)form.Build()).VoltageColumn.Value);
    }
    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1,5")]
    [InlineData("")]
    public async Task InvalidResistanceThresholdKeepsPlanAndBlocksRunUntilDiscard(string text)
    {
        var b = new Bench("源表电阻"); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        var form = vm.NodeForm!.Acquisition!; form.MinimumCurrent = text;
        Assert.True(vm.NodeFragmentDirty); Assert.False(vm.IsValidated); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.True(vm.PreparationForm!.IsReadOnly);
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); await b.AssertDisconnected();
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError); Assert.False(vm.NodeFragmentDirty); Assert.Equal("0", vm.NodeForm!.Acquisition!.MinimumCurrent);
    }
    [Fact]
    public void DuplicateColumnsAndEnvironmentCollisionAreRejectedWithoutDroppingOriginalBindings()
    {
        var form = new PlanAcquisitionFormViewModel(Step("resistanceVoltage"), Catalog(), () => { });
        form.VoltageColumn = form.CurrentColumn; Assert.Throws<ArgumentException>(() => form.Build());
        form.VoltageColumn = "temperature"; Assert.Throws<ArgumentException>(() => form.Build());
        form.VoltageColumn = "new_voltage"; var built = (ReadResistanceStep)form.Build(); Assert.Equal(2, built.EnvironmentReads.Count); Assert.Equal(PhysicalUnit.Ampere, built.MinimumCurrent.Unit);
    }
    [Fact]
    public void IndependentSelectionUsesActualCatalogRolesAndRequiresAFreshExplicitReferenceAndExpectedValue()
    {
        var form = new PlanAcquisitionFormViewModel(Step("independentConstant"), Catalog(), () => { });
        Assert.DoesNotContain(form.Sources, d => d.Descriptor.Model == "K2182"); Assert.All(form.Meters, d => Assert.Equal("K2182", d.Descriptor.Model));
        form.SelectedSource = form.Sources.Single(d => d.Descriptor.Model == "K6221"); Assert.Null(form.SelectedReference); Assert.Empty(form.ExpectedCurrent);
        Assert.Single(form.CurrentReferences); Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedReference = CurrentReference.MeasuredSourceReadback; form.ExpectedCurrent = ".00001"; Assert.Throws<InvalidOperationException>(() => form.Build());
        form.SelectedReference = CurrentReference.ProgrammedSetpoint; Assert.Equal(new InstrumentId("current"), ((ReadCurrentResistanceStep)form.Build()).Source);
        form.SelectedSource = new(new(new("k2450"), "K6221", "Forged UI model", DriverKind.Hardware, DeviceStopPolicy.OutputOff, []));
        Assert.Contains(CurrentReference.MeasuredSourceReadback, form.CurrentReferences);
        form.SelectedReference = CurrentReference.MeasuredSourceReadback; form.ExpectedCurrent = ".00001"; Assert.Equal(new InstrumentId("k2450"), ((ReadCurrentResistanceStep)form.Build()).Source);
        form.SelectedMeter = form.Sources[0]; Assert.Throws<InvalidOperationException>(() => form.Build());
    }
    [Theory]
    [InlineData("negative")]
    [InlineData("infinite")]
    [InlineData("managedNonzero")]
    [InlineData("sampleOwnership")]
    [InlineData("sampleExternal")]
    [InlineData("expectedBelowThreshold")]
    public void RetrySampleAndExpectedCurrentRulesRemainDomainValidated(string damage)
    {
        var form = new PlanAcquisitionFormViewModel(Step(damage.StartsWith("sample", StringComparison.Ordinal) ? "sampleIndependent" : "independentConstant"), Catalog(), () => { });
        if (damage == "negative") form.RetrySettle = "-1";
        else if (damage == "infinite") form.SampleSettle = "NaN";
        else if (damage == "managedNonzero") { form.OwnsSourceOutput = false; form.RetrySettle = ".1"; }
        else if (damage == "sampleOwnership") { form.OwnsSourceOutput = false; form.RetrySettle = "0"; }
        else if (damage == "sampleExternal") form.RoutingManagedExternally = true;
        else form.MinimumCurrent = ".001";
        if (damage == "infinite") form = new(Step("sampleIntegrated"), Catalog(), () => { }) { SampleSettle = "NaN" };
        Assert.ThrowsAny<Exception>(() => form.Build());
    }
    [Theory]
    [InlineData("源与测量读数")]
    [InlineData("源表电阻")]
    [InlineData("独立源电阻")]
    public async Task AcquisitionPaletteRequiresSelectionThenSavesReloadsAndExecutesWithOutputOff(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "acquisition-form-" + Guid.NewGuid().ToString("N") + ".json");
        var b = new Bench(kind, palette: true); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.PaletteKind = kind; Assert.True(vm.HasPaletteForm); vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        var draft = vm.PaletteForm!.Acquisition!;
        draft.SelectedSource = draft.Sources.Single(d => d.Descriptor.Model == (kind == "独立源电阻" ? "K6221" : "K2450"));
        if (kind == "独立源电阻") { draft.SelectedMeter = draft.Meters.Single(); draft.SelectedReference = CurrentReference.ProgrammedSetpoint; draft.UseExpectedCurrent = true; draft.ExpectedCurrent = ".00001"; }
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.NotSame(draft, vm.PaletteForm!.Acquisition); await b.AssertDisconnected();
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var fresh = new Bench(kind, palette: true); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows);
            Assert.Equal(PhysicalUnit.Volt, row.Values[new("voltage_read")].Unit); Assert.Equal(PhysicalUnit.Ampere, row.Values[new("current_read")].Unit);
            if (kind != "源与测量读数") Assert.Equal(PhysicalUnit.Ohm, row.Values[new("resistance")].Unit);
            Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False((await fresh.Current.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
            Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ChangedSourceFunctionConflictingWithPreparationIsRejectedAndBusyOrMixedDraftsRemainInert()
    {
        var b = new Bench("源表电阻"); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.NodeForm!.Acquisition!.SourceFunction = SourceFunction.Current; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        vm.DiscardNodeCommand.Execute(null); var stale = vm.NodeForm!.Acquisition!; stale.VoltageColumn = "new_voltage"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var applied = vm.DocumentText; stale.VoltageColumn = "stale"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.NodeForm!.Acquisition!.IsReadOnly); vm.NodeForm.Acquisition.CurrentColumn = "blocked"; vm.ApplyNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText);
        owner.IsBusy = false; vm.DiscardNodeCommand.Execute(null);
        vm.NodeForm!.Acquisition!.CurrentColumn = "draft"; vm.NodeFragment = vm.NodeFragment + " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(applied, vm.DocumentText);
        vm.DiscardNodeCommand.Execute(null); await b.AssertDisconnected();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SampleAcquisitionEditsSaveFreshReloadAndExecuteTheActualFrozenRouting(bool independent)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples/plans/sample-four-wire.plan.json"))) directory = directory.Parent;
        Assert.NotNull(directory); var codec = new JsonExperimentPlanDocument(); var fixture = codec.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "examples/plans/sample-four-wire.plan.json")));
        var originalSample = (ReadSampleResistanceStep)((ScanStep)fixture.Root).Body; var oldWiring = fixture.Routing!.Wirings.Single();
        var source = new MockKeithley2450SourceMeter(); var current = new MockKeithley6221CurrentSource(new("current"), "DC simulator"); var meter = new MockKeithley2182Meter(new("meter"), "Meter simulator");
        var excitation = independent ? current.Descriptor.Id : source.Id;
        var routing = independent ? new RoutingConfiguration(fixture.Routing.Matrix, fixture.Routing.Channels,
            [new(oldWiring.Id, excitation, oldWiring.SourcePlus, oldWiring.SourceMinus, oldWiring.SensePlus, oldWiring.SenseMinus, oldWiring.Label, meter.Descriptor.Id)], fixture.Routing.Samples) : fixture.Routing;
        var acquisition = independent ? new ReadCurrentResistanceStep(originalSample.Id, excitation, meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint,
            new("V"), new("I"), new("R"), new(1e-12, PhysicalUnit.Ampere), new ConstantValue(new(.00001, PhysicalUnit.Ampere)), new(routing.Matrix, routing.Resolve(originalSample.Binding).Closed), retry: new(true, TimeSpan.Zero, true)) : originalSample.Acquisition;
        var sample = new ReadSampleResistanceStep(acquisition, originalSample.Binding, TimeSpan.Zero);
        var plan = new ExperimentPlan("sample form", independent ? sample : fixture.Root, fixture.Preparations, fixture.Metadata, routing,
            independent ? [new(excitation, new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))] : null, 80);
        var hub = new NotificationHub(); var manager = new InstrumentManager(source, hub);
        var matrix = new MockMatrixSwitch(new(new(new("matrix"), "K3706", "Simulation matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 6)], [excitation]));
        var registry = new DeviceRegistry([manager, new Keithley6221CurrentSourceSession(current, hub), new Keithley2182MeterSession(meter, hub), new MatrixSwitchSession(matrix, hub)], new(TimeSpan.FromSeconds(5)));
        var runs = new MeasurementRunService(registry, hub); var workspace = new ExperimentPlanWorkspace(codec, runs);
        await using var shell = new MainWindowViewModel(manager, runs, hub, workspace, plan, "unused-sample-form.json"); var vm = shell.Plans!;
        vm.GraphNodes.Single(n => n.Id == originalSample.Id).SelectCommand.Execute(null); var form = vm.NodeForm!.Acquisition!;
        Assert.True(form.IsSample); form.MinimumCurrent = "1e-13"; form.SampleSettle = "0.001"; form.AcquisitionSettle = "0.002";
        vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(plan))!["routing"], JsonNode.Parse(vm.DocumentText)!["routing"]));
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await matrix.GetSnapshotAsync(CancellationToken.None)).Connected);
        var path = Path.Combine(Path.GetTempPath(), "sample-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var loaded = await new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs).LoadAsync(path, CancellationToken.None);
            Assert.False(vm.HasError, vm.Message); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            var read = independent ? (ReadSampleResistanceStep)loaded.Plan.Root : (ReadSampleResistanceStep)((ScanStep)loaded.Plan.Root).Body;
            Assert.Equal(TimeSpan.FromSeconds(.001), read.Settle); Assert.Equal(TimeSpan.FromSeconds(.002), read.AcquisitionSettle); Assert.Equal(sample.Binding, read.Binding);
            await runs.StartPlanAsync(loaded.Plan, CancellationToken.None); Assert.Equal(independent ? 1 : 3, runs.LatestDataSet!.Rows.Count);
            Assert.All(runs.LatestDataSet.Rows, row => { loaded.Plan.Routing!.ValidateContext(row.Routing!); Assert.Equal(excitation, row.Routing!.SourceMeter); });
            Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False((await current.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
            Assert.Empty((await matrix.GetSnapshotAsync(CancellationToken.None)).Closed); Assert.False(runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private static IReadOnlyList<DeviceDescriptor> Catalog() => [new MockKeithley2450SourceMeter().Descriptor,
        new MockKeithley6221CurrentSource(new("current"), "DC simulator").Descriptor,
        new MockKeithley2182Meter(new("meter"), "Meter simulator").Descriptor];
    private static ExperimentStep Step(string kind)
    {
        var env = new EnvironmentRead[] { new(EnvironmentRole.Temperature, new("temperature")), new(EnvironmentRole.Field, new("field"), new(new("field-controller"), new("field.readback"))) };
        var function = kind.EndsWith("Current", StringComparison.Ordinal) ? SourceFunction.Current : SourceFunction.Voltage;
        if (kind.StartsWith("source", StringComparison.Ordinal)) return new ReadSourceSenseStep(new("read"), new("k2450"), function, new("V"), new("I"), env);
        if (kind.StartsWith("resistance", StringComparison.Ordinal) || kind == "sampleIntegrated")
        {
            var integrated = new ReadResistanceStep(new("read"), new("k2450"), function, new("V"), new("I"), new("R"), new(1e-12, PhysicalUnit.Ampere), env);
            return kind == "sampleIntegrated" ? new ReadSampleResistanceStep(integrated, new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance), TimeSpan.FromSeconds(.02), true, TimeSpan.FromSeconds(.03)) : integrated;
        }
        PlanValue expected = kind == "independentVariable" ? new VariableValue(new("bias")) : kind == "independentExpression" ? new QuantityFromNumberValue(new("bias"), PhysicalUnit.Ampere) : new ConstantValue(new(.00001, PhysicalUnit.Ampere));
        var independent = new ReadCurrentResistanceStep(new("read"), new("k2450"), new("meter"), CurrentReference.MeasuredSourceReadback, new("V"), new("I"), new("R"), new(1e-12, PhysicalUnit.Ampere), expected,
            new(new("matrix"), [new(1, 1, 1)]), env, new(true, TimeSpan.FromSeconds(.04), true));
        return kind == "sampleIndependent" ? new ReadSampleResistanceStep(independent, new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance), TimeSpan.FromSeconds(.02), acquisitionSettle: TimeSpan.FromSeconds(.03)) : independent;
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockKeithley6221CurrentSource Current { get; } = new(new("current"), "DC simulator");
        public MockKeithley2182Meter Meter { get; } = new(new("meter"), "Meter simulator");
        public MainWindowViewModel Shell { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench(string kind, bool palette = false)
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub);
            var registry = new DeviceRegistry([manager, new Keithley6221CurrentSourceSession(Current, hub), new Keithley2182MeterSession(Meter, hub)], new(TimeSpan.FromSeconds(5)));
            Runs = new(registry, hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var target = kind == "独立源电阻" ? new InstrumentId("current") : Source.Id;
            ExperimentStep read = kind == "源与测量读数" ? new ReadSourceSenseStep(new("read"), target, SourceFunction.Voltage, new("V"), new("I"))
                : kind == "独立源电阻" ? new ReadCurrentResistanceStep(new("read"), target, Meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere))
                : new ReadResistanceStep(new("read"), target, SourceFunction.Voltage, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere));
            var plan = new ExperimentPlan("acquisition forms", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), target, true), .. palette ? Array.Empty<ExperimentStep>() : new[] { read }]),
                [new(Source.Id, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))],
                metadata: new Dictionary<string, Quantity> { ["fixture"] = new(7, PhysicalUnit.Dimensionless) }, currentSourcePreparations: [new(Current.Descriptor.Id, new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))], maximumStepExecutions: 80);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-acquisition.json");
            if (!palette) Shell.Plans!.GraphNodes.Single(n => n.Id.Value == "read").SelectCommand.Execute(null);
        }
        public async Task AssertDisconnected()
        {
            Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Current.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Meter.GetSnapshotAsync(CancellationToken.None)).Connected);
        }
    }
}

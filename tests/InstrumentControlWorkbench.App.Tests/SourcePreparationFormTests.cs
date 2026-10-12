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
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class SourcePreparationFormTests
{
    [Fact]
    public void ExistingSourceMeterRoundTripsEverySettingWithoutInitialDirtyCallbacks()
    {
        var prep = new SourcePreparation(new("k2450"), SourceFunction.Voltage, new(.05, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere),
            new(SenseFunction.Current, SensingMode.TwoWire, .5, false, new(.0001, PhysicalUnit.Ampere), new(false, true, true, 37, AveragingMode.Moving)), new(.2, PhysicalUnit.Volt));
        var calls = 0; var form = new SourcePreparationFormViewModel(Smu(), prep, null, () => calls++);
        Assert.Equal(prep, form.BuildSourceMeter()); Assert.False(form.IsDirty); Assert.Equal(0, calls); Assert.Equal("0.05", form.LevelText);
        form.SourceFunction = SourceFunction.Current;
        Assert.Equal("Ampere", form.LevelUnit); Assert.Equal("Volt", form.ComplianceUnit); Assert.Empty(form.LevelText); Assert.Empty(form.ComplianceText); Assert.Null(form.SourceRange);
        Assert.Throws<FormatException>(() => form.BuildSourceMeter()); Assert.True(calls > 0);
    }
    [Fact]
    public void UnspecifiedFixedSenseRangeAndAcquisitionRemainUnspecified()
    {
        // A legacy fixed range without a requested span is valid.
        var original = Preparation();
        var prep = new SourcePreparation(original.Device, original.Function, original.Level, original.Compliance, new(SenseFunction.Current, SensingMode.FourWire, 1, false));
        var form = new SourcePreparationFormViewModel(Smu(), prep, null, () => { });
        Assert.Equal(prep, form.BuildSourceMeter()); Assert.False(form.UseSenseRange); Assert.Null(form.BuildSourceMeter().Sense.Acquisition);
        form.UseSenseRange = true; Assert.Throws<InvalidOperationException>(() => form.BuildSourceMeter());
        form.SenseRange = form.SenseRanges.Single(r => r.Quantity.Value == .0001); Assert.NotNull(form.BuildSourceMeter().Sense.ManualRange);
        form.SenseFunction = SenseFunction.Voltage; Assert.Null(form.SenseRange);
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1,5")]
    [InlineData("")]
    public async Task InvalidDraftLocksRunFilesImportAndKeepsOriginalUntilDiscard(string value)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = b.Shell.Plans!; var before = vm.DocumentText;
        vm.PreparationForm!.LevelText = value;
        Assert.True(vm.PreparationDirty); Assert.False(vm.IsValidated); Assert.True(vm.IsReadOnly); Assert.True(vm.NodeForm!.IsReadOnly);
        Assert.False(b.Shell.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.False(vm.LoadCommand.CanExecute(null));
        Assert.False(vm.ImportPpmsCommand.CanExecute(null)); Assert.False(vm.CanSelectPreparation); Assert.False(vm.AppendNodeCommand.CanExecute(null));
        vm.ApplyPreparationCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.PreparationDirty);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.DiscardPreparationCommand.Execute(null); Assert.False(vm.HasError); Assert.False(vm.PreparationDirty); Assert.True(b.Shell.StartPlanCommand.CanExecute(null));
    }
    [Fact]
    public async Task SelectedDeviceAndFullDocumentCannotSilentlyDiscardAnUnappliedPreparation()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = b.Shell.Plans!; var before = vm.DocumentText; var selected = vm.SelectedPreparationDevice;
        vm.PreparationForm!.ComplianceText = ".0002";
        vm.SelectedPreparationDevice = vm.PreparationDevices.Single(d => d.Descriptor.Model == "K6221"); Assert.Same(selected, vm.SelectedPreparationDevice);
        vm.DocumentText = "{}"; Assert.Equal(before, vm.DocumentText); Assert.True(vm.PreparationDirty); Assert.True(vm.HasError);
        vm.ApplyPreparationCommand.Execute(null); Assert.False(vm.HasError); Assert.Equal(.0002, Assert.Single(vm.ValidatedPlan!.Preparations).Compliance.Value);
    }
    [Fact]
    public async Task AppliedPreparationPreservesStepsAndOtherSourcesAndIgnoresStaleForms()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = b.Shell.Plans!; var original = vm.ValidatedPlan!; var stale = vm.PreparationForm!;
        stale.LevelText = ".02"; vm.ApplyPreparationCommand.Execute(null);
        Assert.False(vm.HasError, vm.Message); Assert.Same(original.Root, vm.ValidatedPlan!.Root); Assert.Equal(original.CurrentSourcePreparations, vm.ValidatedPlan.CurrentSourcePreparations);
        Assert.Equal(original.Metadata, vm.ValidatedPlan.Metadata); Assert.Equal(80, vm.ValidatedPlan.MaximumStepExecutions); Assert.True(vm.IsDirty);
        stale.LevelText = ".04"; Assert.False(vm.PreparationDirty);
        await b.Shell.StartPlanCommand.ExecuteAsync(null); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }
    [Fact]
    public async Task MixedNodeAndPreparationDraftsRequireExplicitDiscardAndBusyBodiesRemainInert()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = b.Shell.Plans!; var before = vm.DocumentText;
        vm.PreparationForm!.LevelText = ".01"; vm.NodeForm!.Seconds = "0.02";
        vm.ApplyNodeCommand.Execute(null); vm.ApplyPreparationCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.False(vm.IsValidated);
        vm.DiscardPreparationCommand.Execute(null); Assert.True(vm.NodeFragmentDirty); vm.DiscardNodeCommand.Execute(null); Assert.True(vm.IsValidated);
        vm.PreparationForm!.LevelText = ".03"; b.Shell.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.PreparationForm.IsReadOnly);
        vm.ApplyPreparationCommand.Execute(null); vm.DiscardPreparationCommand.Execute(null); vm.RemovePreparationCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.True(vm.PreparationDirty);
        b.Shell.IsBusy = false; vm.IsDocumentBusy = true; vm.ApplyPreparationCommand.Execute(null); Assert.Equal(before, vm.DocumentText);
        vm.IsDocumentBusy = false; vm.DiscardPreparationCommand.Execute(null);
    }
    [Fact]
    public async Task RemovingUnusedPreparationAndAddingItAgainRequiresExplicitPhysicalValues()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = b.Shell.Plans!;
        vm.RemovePreparationCommand.Execute(null); Assert.False(vm.HasError); Assert.Empty(vm.ValidatedPlan!.Preparations); Assert.False(vm.PreparationForm!.HasExistingPreparation);
        Assert.Empty(vm.PreparationForm.LevelText); Assert.Empty(vm.PreparationForm.ComplianceText);
        vm.PreparationForm.LevelText = "0"; vm.PreparationForm.ComplianceText = ".0001"; vm.ApplyPreparationCommand.Execute(null);
        Assert.False(vm.HasError, vm.Message); Assert.Single(vm.ValidatedPlan!.Preparations);
    }
    [Fact]
    public async Task CurrentSourceFormPreservesOptionalDeviceDelayAndRejectsZeroOrForgedRanges()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = b.Shell.Plans!;
        vm.SelectedPreparationDevice = vm.PreparationDevices.Single(d => d.Descriptor.Model == "K6221"); var form = vm.PreparationForm!;
        Assert.True(form.IsCurrentSource); Assert.Null(form.BuildCurrentSource().Settings.SourceDelay);
        form.UseSourceDelay = true; form.SourceDelay = "0"; vm.ApplyPreparationCommand.Execute(null); Assert.True(vm.HasError); Assert.True(vm.PreparationDirty);
        form.SourceDelay = ".01"; form.SourceAutoRange = false; form.SourceRange = new(new(.003, PhysicalUnit.Ampere)); vm.ApplyPreparationCommand.Execute(null); Assert.True(vm.HasError);
        form.SourceRange = form.SourceRanges.Single(r => r.Quantity.Value == .002); vm.ApplyPreparationCommand.Execute(null);
        Assert.False(vm.HasError, vm.Message); Assert.Equal(new Quantity(.01, PhysicalUnit.Second), Assert.Single(vm.ValidatedPlan!.CurrentSourcePreparations).Settings.SourceDelay);
        Assert.False((await b.Current.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.PreparationForm!.UseSourceDelay = false; vm.ApplyPreparationCommand.Execute(null); Assert.Null(Assert.Single(vm.ValidatedPlan!.CurrentSourcePreparations).Settings.SourceDelay);
    }
    [Fact]
    public void AcquisitionRejectsResistanceReboundAndInvalidCountsWithoutChangingTheOriginal()
    {
        var form = new SourcePreparationFormViewModel(Smu(), Preparation(), null, () => { });
        form.UseAcquisition = true; form.AveragingCount = "101"; Assert.Throws<ArgumentOutOfRangeException>(() => form.BuildSourceMeter());
        form.AveragingCount = "10"; form.AutorangeRebound = true; form.SenseFunction = SenseFunction.Resistance; Assert.Throws<NotSupportedException>(() => form.BuildSourceMeter());
        form.AutorangeRebound = false; Assert.Equal(SenseFunction.Resistance, form.BuildSourceMeter().Sense.Function);
    }
    private static DeviceDescriptor Smu() => new MockKeithley2450SourceMeter().Descriptor;
    private static SourcePreparation Preparation() => new(new("k2450"), SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true));
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockKeithley6221CurrentSource Current { get; } = new(new("current"), "Simulated DC source");
        public MainWindowViewModel Shell { get; }
        public Bench()
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub);
            var registry = new DeviceRegistry([manager, new Keithley6221CurrentSourceSession(Current, hub)], new(TimeSpan.FromSeconds(5)));
            var runs = new MeasurementRunService(registry, hub); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs);
            var plan = new ExperimentPlan("preparation forms", new SequenceStep(new("root"), [new DelayStep(new("delay"), TimeSpan.Zero)]), [Preparation()],
                metadata: new Dictionary<string, Quantity> { ["fixture"] = new(7, PhysicalUnit.Dimensionless) },
                currentSourcePreparations: [new(new("current"), new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))], maximumStepExecutions: 80);
            Shell = new(manager, runs, hub, workspace, plan, "unused-preparations.json");
            Shell.Plans!.GraphNodes.Single(n => n.Id.Value == "delay").SelectCommand.Execute(null);
        }
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanSampleCatalogFormTests
{
    [Theory]
    [InlineData(SampleTopology.TwoWire, false, false)]
    [InlineData(SampleTopology.FourWire, false, false)]
    [InlineData(SampleTopology.HallBar, false, false)]
    [InlineData(SampleTopology.VanDerPauw, false, false)]
    [InlineData(SampleTopology.FourWire, true, false)]
    [InlineData(SampleTopology.HallBar, true, false)]
    [InlineData(SampleTopology.VanDerPauw, true, false)]
    [InlineData(SampleTopology.FourWire, true, true)]
    public async Task InitialCatalogAndNewReadSaveReloadAndExecuteWithoutImportedProfiles(SampleTopology topology, bool independent, bool smuSource)
    {
        var b = new Bench(topology, independent, smuSource, noCatalog: true); await using var owner = b.Shell; var vm = owner.Plans!;
        vm.PaletteKind = independent ? "独立源样品电阻" : "样品电阻";
        Assert.Empty(vm.PaletteForm!.Acquisition!.SampleRouting!.Samples);
        Assert.True(vm.CanInitializeRouting); vm.InitializeRoutingCommand.Execute(null);
        var form = vm.RoutingInitializationForm!; Assert.True(vm.SampleDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        AuthorInitial(form, topology, independent, smuSource ? b.Source.Id.Value : independent ? "current" : b.Source.Id.Value);
        vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(vm.HasRoutingCatalog); Assert.False(vm.InitializingRouting); Assert.False(vm.CanInitializeRouting);
        Assert.Same(b.Plan.Root, vm.ValidatedPlan!.Root); Assert.Equal(b.Plan.Preparations, vm.ValidatedPlan.Preparations); Assert.Equal(b.Plan.CurrentSourcePreparations, vm.ValidatedPlan.CurrentSourcePreparations); Assert.Equal(b.Plan.MaximumStepExecutions, vm.ValidatedPlan.MaximumStepExecutions);
        Assert.Single(vm.PaletteForm!.Acquisition!.SampleRouting!.Samples); await b.AssertNoIO();
        var before = vm.DocumentText; var acquisition = vm.PaletteForm.Acquisition; Assert.Empty(acquisition.MinimumCurrent);
        var binding = acquisition.SampleRouting!; binding.SelectedSample = binding.Samples[0]; binding.SelectedWiring = binding.Wirings[0]; binding.SelectedMeasurement = binding.Measurements[0];
        if (independent) acquisition.SelectedReference = CurrentReference.ProgrammedSetpoint;
        vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        acquisition.MinimumCurrent = "1e-12"; vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var path = Path.Combine(Path.GetTempPath(), "initial-routing-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var fresh = new Bench(topology, independent, smuSource, noCatalog: true); await using var freshOwner = fresh.Shell;
            var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            var read = (ReadSampleResistanceStep)Assert.Single(((SequenceStep)loaded.Plan.Root).Children); var route = loaded.Plan.Routing!.Resolve(read.Binding);
            if (independent) Assert.Equal(new MatrixRouteExpectation(route.Matrix, route.Closed), ((ReadCurrentResistanceStep)read.Acquisition).RequiredRoute);
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows);
            Assert.Equal("initial-sample", row.Routing!.Binding.Sample.Value); Assert.Equal(route.Roles, row.Routing.Roles); Assert.True(row.Values.ContainsKey(read.Acquisition.ResistanceColumn)); await fresh.AssertCleanup();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("matrix")]
    [InlineData("source")]
    [InlineData("meter")]
    [InlineData("sampleId")]
    [InlineData("sampleName")]
    [InlineData("wiringId")]
    [InlineData("channelId")]
    [InlineData("channelName")]
    [InlineData("topology")]
    [InlineData("duplicateKey")]
    [InlineData("duplicateTerminal")]
    [InlineData("unknownSlot")]
    [InlineData("outsideRow")]
    [InlineData("laterDirectionOutsideCard")]
    [InlineData("fraction")]
    [InlineData("zero")]
    [InlineData("overflow")]
    [InlineData("blank")]
    public async Task InvalidInitialCatalogRetainsNoCatalogPlanAndSendsNoCommands(string damage)
    {
        var b = new Bench(SampleTopology.HallBar, true, noCatalog: true); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.InitializeRoutingCommand.Execute(null); var form = vm.RoutingInitializationForm!; AuthorInitial(form, SampleTopology.HallBar, true, "current");
        switch (damage)
        {
            case "matrix": form.SelectedMatrix = null; break;
            case "source": form.SelectedSource = null; break;
            case "meter": form.SelectedMeter = null; break;
            case "sampleId": form.SampleIdText = ""; break;
            case "sampleName": form.Details.SampleName = ""; break;
            case "wiringId": form.WiringIdText = ""; break;
            case "channelId": form.ChannelIdText = ""; break;
            case "channelName": form.ChannelName = ""; break;
            case "topology": form.Topology = null; break;
            case "duplicateKey": form.Connections[1].KeyText = form.Connections[0].KeyText.ToUpperInvariant(); break;
            case "duplicateTerminal": form.Connections[1].ColumnText = form.Connections[0].ColumnText; break;
            case "unknownSlot": form.Connections[0].SlotText = "2"; break;
            case "outsideRow": form.SourcePlus.RowText = "5"; break;
            case "laterDirectionOutsideCard": form.Connections[^1].ColumnText = "13"; break;
            case "fraction": form.SourcePlus.RowText = "1.5"; break;
            case "zero": form.SourcePlus.RowText = "0"; break;
            case "overflow": form.SourcePlus.RowText = "2147483648"; break;
            default: form.SourcePlus.RowText = ""; break;
        }
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.SampleDirty); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.False(owner.StartPlanCommand.CanExecute(null)); await b.AssertNoIO();
        vm.DiscardSampleCommand.Execute(null); Assert.False(vm.SampleDirty); Assert.Null(vm.RoutingInitializationForm); Assert.True(vm.CanInitializeRouting); Assert.Null(vm.ValidatedPlan!.Routing);
    }
    [Fact]
    public async Task InitialDraftGatesBusyAndIgnoresRetiredFormAndConnectionCallbacks()
    {
        var b = new Bench(SampleTopology.FourWire, noCatalog: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.InitializeRoutingCommand.Execute(null); var form = vm.RoutingInitializationForm!;
        Assert.True(vm.NodeForm!.IsReadOnly); Assert.True(vm.PreparationForm!.IsReadOnly); Assert.False(vm.AppendNodeCommand.CanExecute(null)); Assert.False(vm.InitializeRoutingCommand.CanExecute(null));
        AuthorInitial(form, SampleTopology.FourWire, false, b.Source.Id.Value); var before = vm.DocumentText;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(form.IsReadOnly); Assert.True(form.Details.IsReadOnly); Assert.All(form.Ports, p => Assert.True(p.IsReadOnly)); Assert.All(form.Connections, c => Assert.True(c.IsReadOnly));
        vm.ApplySampleCommand.Execute(null); Assert.Equal(before, vm.DocumentText); owner.IsBusy = false; vm.DiscardSampleCommand.Execute(null); form.Details.Material = "stale"; Assert.False(vm.SampleDirty);
        var callbacks = 0; var independentForm = new PlanRoutingInitializationFormViewModel(b.Workspace.Devices, b.Workspace.MatrixDefinitions, () => callbacks++); Assert.Equal(0, callbacks);
        independentForm.Topology = SampleTopology.FourWire; var row = independentForm.Connections[0]; independentForm.Topology = SampleTopology.HallBar; var count = callbacks; row.KeyText = "stale"; Assert.Equal(count, callbacks); Assert.All(independentForm.Connections, c => Assert.Empty(c.ColumnText)); await b.AssertNoIO();
    }
    [Fact]
    public async Task DeviceChangesClearPhysicalAddressesAndCatalogCannotBeReinitialized()
    {
        var b = new Bench(SampleTopology.HallBar, noCatalog: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.InitializeRoutingCommand.Execute(null); var form = vm.RoutingInitializationForm!;
        AuthorInitial(form, SampleTopology.HallBar, false, b.Source.Id.Value); form.SelectedSource = form.Sources.Single(s => s.Descriptor.Id.Value == "current"); Assert.All(form.Ports, p => Assert.Empty(p.RowText));
        AuthorInitial(form, SampleTopology.HallBar, false, b.Source.Id.Value); form.SelectedMatrix = null; Assert.All(form.Ports, p => Assert.Empty(p.RowText)); Assert.All(form.Connections, c => Assert.Empty(c.ColumnText));
        AuthorInitial(form, SampleTopology.HallBar, false, b.Source.Id.Value); form.UseIndependentMeter = true; Assert.All(form.Ports, p => Assert.Empty(p.RowText));
        AuthorInitial(form, SampleTopology.HallBar, true, "current"); form.SelectedMeter = null; Assert.Empty(form.SensePlus.RowText); Assert.Empty(form.SenseMinus.RowText);
        AuthorInitial(form, SampleTopology.HallBar, false, b.Source.Id.Value); vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var before = vm.DocumentText;
        Assert.Throws<InvalidOperationException>(() => b.Workspace.InitializeRouting(vm.ValidatedPlan!, vm.ValidatedPlan!.Routing!)); vm.InitializeRoutingCommand.Execute(null); Assert.Equal(before, vm.DocumentText); await b.AssertNoIO();
    }
    [Fact]
    public async Task CanonicalChoicesUseRegisteredBoundsAndRejectCurrentSourceWithoutMeter()
    {
        var b = new Bench(SampleTopology.HallBar, noCatalog: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.InitializeRoutingCommand.Execute(null); var form = vm.RoutingInitializationForm!;
        AuthorInitial(form, SampleTopology.HallBar, false, b.Source.Id.Value);
        var actual = form.Matrices[0].Definition;
        form.SelectedMatrix = new(new(actual.Device, [new(1, 100, 100)], actual.GuardedSources));
        Assert.Contains("4 行 × 12 列", form.MatrixSummary); Assert.DoesNotContain("100", form.MatrixSummary);
        var descriptor = form.SelectedSource!.Descriptor; form.SelectedSource = new(new(descriptor.Id, "forged", "forged", DriverKind.Hardware, DeviceStopPolicy.AbortAcquisition, []));
        for (var i = 0; i < form.Ports.Count; i++) { form.Ports[i].SlotText = "1"; form.Ports[i].RowText = (i + 1).ToString(CultureInfo.InvariantCulture); }
        for (var i = 0; i < form.Connections.Count; i++) { form.Connections[i].SlotText = "1"; form.Connections[i].ColumnText = (i + 1).ToString(CultureInfo.InvariantCulture); }
        form.Connections[^1].ColumnText = "13"; vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.False(vm.HasRoutingCatalog);
        form.Connections[^1].ColumnText = "6"; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Equal(b.Source.Id, vm.ValidatedPlan!.Routing!.Wirings[0].SourceMeter);
        var candidate = new PlanRoutingInitializationFormViewModel(b.Workspace.Devices, b.Workspace.MatrixDefinitions, () => { }); AuthorInitial(candidate, SampleTopology.HallBar, false, "current"); Assert.Throws<InvalidOperationException>(() => candidate.Build());
        var empty = new PlanRoutingInitializationFormViewModel([], [], () => { }); Assert.Empty(empty.Matrices); Assert.Empty(empty.Sources); Assert.Throws<InvalidOperationException>(() => empty.Build()); await b.AssertNoIO();
    }
    [Theory]
    [InlineData("unusedSenseRow")]
    [InlineData("unusedSenseSlot")]
    public async Task TwoWireInitializationAlsoChecksCurrentlyUnusedPhysicalSensePorts(string damage)
    {
        var b = new Bench(SampleTopology.TwoWire, noCatalog: true); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.InitializeRoutingCommand.Execute(null); AuthorInitial(vm.RoutingInitializationForm!, SampleTopology.TwoWire, false, b.Source.Id.Value);
        if (damage == "unusedSenseRow") vm.RoutingInitializationForm!.SensePlus.RowText = "5"; else vm.RoutingInitializationForm!.SensePlus.SlotText = "2";
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.False(vm.HasRoutingCatalog); await b.AssertNoIO();
    }
    private static void AuthorInitial(PlanRoutingInitializationFormViewModel form, SampleTopology topology, bool independent, string source)
    {
        form.SelectedMatrix = form.Matrices[0]; form.SelectedSource = form.Sources.Single(s => s.Descriptor.Id.Value == source); form.UseIndependentMeter = independent;
        if (independent) form.SelectedMeter = form.Meters[0];
        form.WiringIdText = "initial-wire"; form.WiringLabel = "Initial wiring"; form.ChannelIdText = "initial-channel"; form.ChannelName = "Initial channel";
        form.SampleIdText = "initial-sample"; form.Details.SampleName = "Initial sample"; form.Topology = topology;
        for (var i = 0; i < form.Ports.Count; i++) { form.Ports[i].SlotText = "1"; form.Ports[i].RowText = (i + 1).ToString(CultureInfo.InvariantCulture); }
        for (var i = 0; i < form.Connections.Count; i++) { form.Connections[i].KeyText = "c" + i; form.Connections[i].SlotText = "1"; form.Connections[i].ColumnText = (i + 1).ToString(CultureInfo.InvariantCulture); }
    }
}

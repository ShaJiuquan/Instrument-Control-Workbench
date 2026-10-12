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
    public async Task CatalogPhysicalEditsCoordinateExplicitGuardsSaveReloadAndRunNewRows(SampleTopology topology, bool independent, bool smuSource)
    {
        var b = new Bench(topology, independent, smuSource); await using var owner = b.Shell; var vm = owner.Plans!; var old = vm.ValidatedPlan!; var before = vm.DocumentText;
        vm.PaletteKind = independent ? "独立源样品电阻" : "样品电阻"; var oldPalette = vm.PaletteForm;
        vm.EditRoutingCatalogCommand.Execute(null); var form = vm.RoutingCatalogForm!; Assert.True(vm.EditingRoutingCatalog); Assert.True(vm.SampleDirty); Assert.True(vm.SampleForm!.IsReadOnly);
        form.SelectedChannel!.ChannelName = "Changed CH1"; form.SelectedChannel.Notes = "Edited notes";
        for (var i = 0; i < form.SelectedChannel.Connections.Count; i++) form.SelectedChannel.Connections[i].ColumnText = (i + 7).ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < form.SelectedWiring!.Ports.Count; i++) form.SelectedWiring.Ports[i].RowText = ((i + 2) % 4 + 1).ToString(CultureInfo.InvariantCulture);
        Assert.False(form.CoordinateIndependentRoutes);
        if (independent) { vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); form.CoordinateIndependentRoutes = true; }
        vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False(vm.SampleDirty); Assert.False(vm.EditingRoutingCatalog); Assert.NotSame(oldPalette, vm.PaletteForm); await b.AssertNoIO();
        Assert.Equal("Changed CH1", vm.ValidatedPlan!.Routing!.Channels[0].Name); Assert.All(old.Routing!.Channels[0].Connections, c => Assert.InRange(c.Column!.Value, 1, 6)); Assert.Equal(1, old.Routing.Wirings[0].SourcePlus.Row);
        var read = (ReadSampleResistanceStep)((SequenceStep)vm.ValidatedPlan.Root).Children[0]; var route = vm.ValidatedPlan.Routing.Resolve(read.Binding); Assert.All(route.Roles, role => Assert.InRange(role.Point.Column, 7, 12)); Assert.Equal(3, route.Roles.Single(role => role.Role == RoutingRole.SourcePlus).Point.Row);
        if (independent) Assert.Equal(new MatrixRouteExpectation(route.Matrix, route.Closed), ((ReadCurrentResistanceStep)read.Acquisition).RequiredRoute); else Assert.Same(old.Root, vm.ValidatedPlan.Root);
        await SaveFreshCatalogRun(b, vm, topology, independent, smuSource, 1, rows => { var row = Assert.Single(rows); Assert.Equal(route.Roles, row.Routing!.Roles); Assert.True(row.Values.ContainsKey(new("R"))); });
    }
    [Theory]
    [InlineData(SampleTopology.TwoWire, false)]
    [InlineData(SampleTopology.FourWire, false)]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.FourWire, true)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task NewChannelWiringAndSampleBindSecondReadWithoutChangingFirstRun(SampleTopology topology, bool independent)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var vm = owner.Plans!; var old = vm.ValidatedPlan!;
        vm.EditRoutingCatalogCommand.Execute(null); var form = vm.RoutingCatalogForm!; form.AddChannelCommand.Execute(null); var channel = form.SelectedChannel!;
        channel.IdText = "new-channel"; channel.ChannelName = "New channel"; var labels = SampleProfile.RequiredLabels(topology);
        while (channel.Connections.Count < labels.Count) channel.AddConnectionCommand.Execute(null);
        for (var i = 0; i < channel.Connections.Count; i++) { var row = channel.Connections[i]; row.KeyText = "n" + i; row.SlotText = "1"; row.ColumnText = (i + 7).ToString(CultureInfo.InvariantCulture); }
        form.AddWiringCommand.Execute(null); var wire = form.SelectedWiring!; wire.IdText = "new-wire"; wire.Description = "New wiring"; wire.SelectedSource = wire.Sources.Single(s => s.Descriptor.Id.Value == (independent ? "current" : b.Source.Id.Value)); wire.UseIndependentMeter = independent; if (independent) wire.SelectedMeter = wire.Meters[0];
        for (var i = 0; i < wire.Ports.Count; i++) { wire.Ports[i].SlotText = "1"; wire.Ports[i].RowText = ((i + 2) % 4 + 1).ToString(CultureInfo.InvariantCulture); }
        vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Same(old.Root, vm.ValidatedPlan!.Root); Assert.Equal(3, vm.ValidatedPlan.Routing!.Channels.Count); Assert.Equal(2, vm.ValidatedPlan.Routing.Wirings.Count);
        vm.CreateSampleCommand.Execute(null); var sample = vm.CreationForm!; sample.SampleIdText = "third"; sample.Details.SampleName = "Third"; sample.Topology = topology; sample.Mapping!.SelectedWiring = sample.Mapping.Wirings.Single(w => w.Profile.Id.Value == "new-wire");
        for (var i = 0; i < sample.Mapping.Electrodes.Count; i++) { var electrode = sample.Mapping.Electrodes[i]; electrode.SelectedChannel = electrode.Channels.Single(c => c.Profile.Id.Value == "new-channel"); electrode.SelectedConnection = electrode.Connections.Single(c => c.Connection.Key.Value == "n" + i); }
        vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); vm.PaletteKind = independent ? "独立源样品电阻" : "样品电阻"; var acquisition = vm.PaletteForm!.Acquisition!; var binding = acquisition.SampleRouting!;
        binding.SelectedSample = binding.Samples.Single(s => s.Profile.Id.Value == "third"); binding.SelectedWiring = binding.Wirings.Single(w => w.Profile.Id.Value == "new-wire"); binding.SelectedMeasurement = binding.Measurements[0]; acquisition.MinimumCurrent = "1e-12"; if (independent) acquisition.SelectedReference = CurrentReference.ProgrammedSetpoint;
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); await b.AssertNoIO();
        await SaveFreshCatalogRun(b, vm, topology, independent, false, 2, rows => { Assert.Equal("primary", rows[0].Routing!.Binding.Sample.Value); Assert.All(rows[0].Routing!.Roles, role => Assert.InRange(role.Point.Column, 1, 6)); Assert.Equal("third", rows[1].Routing!.Binding.Sample.Value); Assert.All(rows[1].Routing!.Roles, role => Assert.InRange(role.Point.Column, 7, 12)); });
    }
    [Theory]
    [InlineData("referencedConnection")]
    [InlineData("dormantSampleChannel")]
    [InlineData("referencedWiring")]
    [InlineData("duplicateChannel")]
    [InlineData("duplicateWiring")]
    [InlineData("outsideColumn")]
    [InlineData("outsideRow")]
    [InlineData("disabledRequired")]
    [InlineData("changedSource")]
    [InlineData("missingAddress")]
    [InlineData("laterHallDirection")]
    public async Task InvalidCatalogDraftCannotReplacePlanOrSendCommands(string damage)
    {
        var b = new Bench(SampleTopology.HallBar); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; vm.EditRoutingCatalogCommand.Execute(null); var form = vm.RoutingCatalogForm!;
        switch (damage)
        {
            case "referencedConnection": form.SelectedChannel!.RemoveConnectionCommand.Execute(form.SelectedChannel.Connections[0]); break;
            case "dormantSampleChannel": form.RemoveChannelCommand.Execute(null); break;
            case "referencedWiring": form.AddWiringCommand.Execute(null); var otherWire = form.SelectedWiring!; otherWire.IdText = "replacement"; otherWire.SelectedSource = otherWire.Sources.Single(s => s.Descriptor.Id == b.Source.Id); for (var i = 0; i < otherWire.Ports.Count; i++) { otherWire.Ports[i].SlotText = "1"; otherWire.Ports[i].RowText = (i + 1).ToString(CultureInfo.InvariantCulture); } form.SelectedWiring = form.Wirings[0]; form.RemoveWiringCommand.Execute(null); break;
            case "duplicateChannel": form.AddChannelCommand.Execute(null); form.SelectedChannel!.IdText = "CHANNEL1"; form.SelectedChannel.ChannelName = "Duplicate"; var row = form.SelectedChannel.Connections[0]; row.KeyText = "a"; row.SlotText = "1"; row.ColumnText = "1"; break;
            case "duplicateWiring": form.AddWiringCommand.Execute(null); var wire = form.SelectedWiring!; wire.IdText = "WIRE"; wire.SelectedSource = wire.Sources.Single(s => s.Descriptor.Id == b.Source.Id); for (var i = 0; i < wire.Ports.Count; i++) { wire.Ports[i].SlotText = "1"; wire.Ports[i].RowText = (i + 1).ToString(CultureInfo.InvariantCulture); } break;
            case "outsideColumn": form.SelectedChannel!.Connections[0].ColumnText = "13"; break;
            case "laterHallDirection": form.SelectedChannel!.Connections[^1].ColumnText = form.SelectedChannel.Connections[0].ColumnText; break;
            case "outsideRow": form.SelectedWiring!.Ports[0].RowText = "5"; break;
            case "disabledRequired": form.SelectedChannel!.Connections[0].Enabled = false; break;
            case "changedSource": form.SelectedWiring!.SelectedSource = form.SelectedWiring.Sources.Single(s => s.Descriptor.Id.Value == "current"); break;
            default: form.SelectedChannel!.Connections[0].SlotText = ""; break;
        }
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.SampleDirty); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null)); await b.AssertNoIO(); vm.DiscardSampleCommand.Execute(null); Assert.False(vm.EditingRoutingCatalog); Assert.False(vm.SampleDirty);
    }
    [Fact]
    public async Task BusyStaleAndRemovedFormCallbacksStayInsideTheirDraft()
    {
        var b = new Bench(SampleTopology.FourWire); await using var owner = b.Shell; var vm = owner.Plans!; vm.EditRoutingCatalogCommand.Execute(null); var form = vm.RoutingCatalogForm!;
        Assert.False(vm.CreateSampleCommand.CanExecute(null)); Assert.False(vm.InitializeRoutingCommand.CanExecute(null)); Assert.True(vm.NodeForm!.IsReadOnly); Assert.True(vm.PreparationForm!.IsReadOnly); var before = vm.DocumentText;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(form.IsReadOnly); Assert.All(form.Channels, c => { Assert.True(c.IsReadOnly); Assert.All(c.Connections, r => Assert.True(r.IsReadOnly)); }); Assert.All(form.Wirings, w => Assert.All(w.Ports, p => Assert.True(p.IsReadOnly))); Assert.False(form.AddChannelCommand.CanExecute(null)); vm.ApplySampleCommand.Execute(null); Assert.Equal(before, vm.DocumentText);
        owner.IsBusy = false; vm.DiscardSampleCommand.Execute(null); form.Channels[0].Notes = "late"; Assert.False(vm.SampleDirty);
        var calls = 0; var local = new PlanRoutingCatalogFormViewModel(b.Plan.Routing!, b.Workspace.Devices, b.Workspace.MatrixDefinitions, true, () => calls++); Assert.Equal(0, calls); local.AddChannelCommand.Execute(null); var removed = local.SelectedChannel!; local.RemoveChannelCommand.Execute(null); var count = calls; removed.ChannelName = "retired"; Assert.Equal(count, calls);
        var live = local.SelectedChannel!; live.AddConnectionCommand.Execute(null); var connection = live.Connections[^1]; live.RemoveConnectionCommand.Execute(connection); count = calls; connection.Notes = "retired"; Assert.Equal(count, calls); await b.AssertNoIO();
    }
    [Fact]
    public void FormsPreserveOriginalMetadataAndIdentityAndCanonicalDeviceChoices()
    {
        var oldConnection = new ChannelConnection(new("c"), null, null, false, 8, "Label", 9, "raw", "Notes"); var old = new ChannelProfile(new("ch"), "CH", [oldConnection], "legacy", 2, "Alias", "Old");
        var form = new PlanChannelProfileFormViewModel(old, () => { }); Assert.Same(old, form.Build()); form.IdText = "forged"; form.ChannelName = "Changed"; form.Connections[0].KeyText = "forged"; form.Connections[0].Notes = "New"; var built = form.Build(); Assert.Equal(old.Id, built.Id); Assert.Equal(old.OriginalId, built.OriginalId); Assert.Equal(old.GroupIndex, built.GroupIndex); Assert.Equal(old.Alias, built.Alias); var row = Assert.Single(built.Connections); Assert.Equal(oldConnection.Key, row.Key); Assert.Equal(8, row.Index); Assert.Equal(9, row.DisplayRow); Assert.Equal("raw", row.Raw); Assert.Null(row.Slot); Assert.False(row.Enabled);
        var source = new DeviceDescriptor(new("source"), "K2450", "Source", DriverKind.Simulation, DeviceStopPolicy.OutputOff, []); var wire = new InstrumentWiringProfile(new("wire"), source.Id, new(1, 1), new(1, 2), new(1, 3), new(1, 4)); var wiring = new PlanWiringProfileFormViewModel(wire, [source], () => { }); Assert.Same(wire, wiring.Build()); wiring.IdText = "forged"; wiring.SelectedSource = new(new(source.Id, "forged", "forged", DriverKind.Hardware, DeviceStopPolicy.AbortAcquisition, [])); Assert.All(wiring.Ports, p => Assert.Empty(p.RowText)); for (var i = 0; i < 4; i++) { wiring.Ports[i].SlotText = "1"; wiring.Ports[i].RowText = (i + 1).ToString(CultureInfo.InvariantCulture); }
        Assert.Equal(wire.Id, wiring.Build().Id); Assert.Equal(source.Id, wiring.Build().SourceMeter);
    }
    [Fact]
    public async Task CatalogGuardCoordinationUpdatesBothSamplesInsideNestedRepeat()
    {
        var b = new Bench(SampleTopology.FourWire, true); await using var owner = b.Shell; var vm = owner.Plans!; var original = vm.ValidatedPlan!; var first = (ReadSampleResistanceStep)((SequenceStep)original.Root).Children[0]; var acquisition = (ReadCurrentResistanceStep)first.Acquisition;
        var second = new ReadSampleResistanceStep(new ReadCurrentResistanceStep(new("second"), acquisition.Source, acquisition.Meter, acquisition.CurrentReference, acquisition.VoltageColumn, acquisition.CurrentColumn, acquisition.ResistanceColumn, acquisition.MinimumCurrent, acquisition.ExpectedProgrammedCurrent, acquisition.RequiredRoute, acquisition.EnvironmentReads, acquisition.Retry), new(new("unused"), first.Binding.Wiring, first.Binding.Selection), first.Settle);
        var plan = new ExperimentPlan(original.Name, new SequenceStep(new("root"), [new RepeatStep(new("repeat"), 2, new SequenceStep(new("body"), [first, second]))]), original.Preparations, original.Metadata, original.Routing, original.CurrentSourcePreparations, original.MaximumStepExecutions);
        vm.DocumentText = new InstrumentControlWorkbench.Infrastructure.Plans.JsonExperimentPlanDocument().Serialize(plan); vm.ValidateCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        vm.EditRoutingCatalogCommand.Execute(null); var form = vm.RoutingCatalogForm!; for (var i = 0; i < form.SelectedChannel!.Connections.Count; i++) form.SelectedChannel.Connections[i].ColumnText = (i + 7).ToString(CultureInfo.InvariantCulture); form.CoordinateIndependentRoutes = true; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        await SaveFreshCatalogRun(b, vm, SampleTopology.FourWire, true, false, 4, rows => { Assert.Equal(new[] { "primary", "unused", "primary", "unused" }, rows.Select(row => row.Routing!.Binding.Sample.Value)); Assert.All(rows, row => Assert.All(row.Routing!.Roles, role => Assert.InRange(role.Point.Column, 7, 12))); });
    }
    [Fact]
    public async Task CatalogGuardCoordinationCannotChangeReferencedSourceIdentity()
    {
        var b = new Bench(SampleTopology.FourWire, true); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; vm.EditRoutingCatalogCommand.Execute(null); var form = vm.RoutingCatalogForm!; var wiring = form.SelectedWiring!;
        wiring.SelectedSource = wiring.Sources.Single(source => source.Descriptor.Id == b.Source.Id); for (var i = 0; i < 4; i++) { wiring.Ports[i].SlotText = "1"; wiring.Ports[i].RowText = (i + 1).ToString(CultureInfo.InvariantCulture); }
        form.CoordinateIndependentRoutes = true; vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Contains("source or meter", vm.Message); Assert.Equal(before, vm.DocumentText); await b.AssertNoIO();
    }
    [Fact]
    public async Task CatalogAdmissionRequiresTypedStructureInsteadOfSilentlySkippingDirectionChecks()
    {
        var b = new Bench(SampleTopology.FourWire); await using var owner = b.Shell;
        var workspace = new InstrumentControlWorkbench.Application.Experiments.ExperimentPlanWorkspace(new BasicCatalogDocument(), b.Runs);
        Assert.Throws<NotSupportedException>(() => workspace.UpdateRoutingCatalog(b.Plan, b.Plan.Routing!.Channels, b.Plan.Routing.Wirings)); await b.AssertNoIO();
    }
    private sealed class BasicCatalogDocument : IExperimentPlanDocument
    {
        private readonly InstrumentControlWorkbench.Infrastructure.Plans.JsonExperimentPlanDocument codec = new();
        public string Serialize(ExperimentPlan plan) => codec.Serialize(plan);
        public ExperimentPlan Parse(string text) => codec.Parse(text);
        public Task<ExperimentPlan> LoadAsync(string path, CancellationToken token) => codec.LoadAsync(path, token);
        public Task SaveAsync(string path, ExperimentPlan plan, CancellationToken token) => codec.SaveAsync(path, plan, token);
    }
    private static async Task SaveFreshCatalogRun(Bench b, PlanWorkspaceViewModel vm, SampleTopology topology, bool independent, bool smuSource, int count, Action<IReadOnlyList<InstrumentControlWorkbench.Core.Data.MeasurementDataRow>> inspect)
    {
        var path = Path.Combine(Path.GetTempPath(), "catalog-editor-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message); var fresh = new Bench(topology, independent, smuSource); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var rows = fresh.Runs.LatestDataSet!.Rows; Assert.Equal(count, rows.Count); inspect(rows); await fresh.AssertCleanup();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

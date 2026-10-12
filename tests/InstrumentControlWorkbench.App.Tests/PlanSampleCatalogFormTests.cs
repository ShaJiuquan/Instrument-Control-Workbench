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
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanSampleCatalogFormTests
{
    [Theory]
    [InlineData(SampleTopology.TwoWire, false)]
    [InlineData(SampleTopology.FourWire, false)]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.FourWire, true)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task NewSampleCatalogSavesReloadsRebindsRealReadAndExecutesNewElectrodePoints(SampleTopology topology, bool independent)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var vm = owner.Plans!; var original = vm.ValidatedPlan!; vm.CreateSampleCommand.Execute(null);
        Assert.True(vm.CreatingSample); Assert.True(vm.SampleDirty); Author(vm.CreationForm!, topology); vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False(vm.CreatingSample); Assert.Equal("created", vm.SelectedSample!.Profile.Id.Value);
        var created = vm.ValidatedPlan!.Routing!.Samples.Single(sample => sample.Id.Value == "created"); Assert.Equal("New sample", created.Name); Assert.Equal(SampleProfile.RequiredLabels(topology), created.Electrodes.Select(e => e.Label.Value)); Assert.All(created.Electrodes, e => Assert.Equal("channel2", e.Channel.Value));
        Assert.Same(original.Root, vm.ValidatedPlan.Root); Assert.Equal(2, original.Routing!.Samples.Count); Assert.Equal(3, vm.ValidatedPlan.Routing.Samples.Count);
        vm.GraphNodes.Single(node => node.Id.Value == "read").SelectCommand.Execute(null); var binding = vm.NodeForm!.Acquisition!.SampleRouting!; var selection = binding.SelectedMeasurement;
        binding.SelectedSample = binding.Samples.Single(sample => sample.Profile.Id.Value == "created"); binding.SelectedMeasurement = selection; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var path = Path.Combine(Path.GetTempPath(), "sample-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(topology, independent); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            var read = (ReadSampleResistanceStep)((SequenceStep)loaded.Plan.Root).Children[0]; var route = loaded.Plan.Routing!.Resolve(read.Binding); Assert.All(route.Roles, role => Assert.InRange(role.Point.Column, 7, 12));
            if (independent) Assert.Equal(new MatrixRouteExpectation(route.Matrix, route.Closed), ((ReadCurrentResistanceStep)read.Acquisition).RequiredRoute);
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows); Assert.Equal("created", row.Routing!.Binding.Sample.Value); Assert.Equal(route.Roles, row.Routing.Roles); Assert.True(row.Values.ContainsKey(new("R"))); await fresh.AssertCleanup();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("emptyId")]
    [InlineData("emptyName")]
    [InlineData("missingTopology")]
    [InlineData("missingConnection")]
    [InlineData("missingWiring")]
    [InlineData("duplicateId")]
    [InlineData("disabled")]
    [InlineData("duplicateTerminal")]
    [InlineData("outsideCard")]
    public async Task InvalidNewSampleRetainsPlanWithoutConnecting(string damage)
    {
        var b = new Bench(SampleTopology.HallBar); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; vm.CreateSampleCommand.Execute(null); var form = vm.CreationForm!; Author(form, SampleTopology.HallBar);
        switch (damage)
        {
            case "emptyId": form.SampleIdText = ""; break;
            case "emptyName": form.Details.SampleName = ""; break;
            case "missingTopology": form.Topology = null; break;
            case "missingConnection": form.Mapping!.Electrodes[0].SelectedConnection = null; break;
            case "missingWiring": form.Mapping!.SelectedWiring = null; break;
            case "duplicateId": form.SampleIdText = "PRIMARY"; break;
            case "disabled": form.Mapping!.Electrodes[0].Enabled = false; break;
            case "duplicateTerminal": form.Mapping!.Electrodes[1].SelectedConnection = form.Mapping.Electrodes[0].SelectedConnection; break;
            default: form.Mapping!.Electrodes[5].SelectedConnection = form.Mapping.Electrodes[5].Connections.Single(choice => choice.Connection.Key.Value == "outside"); break;
        }
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.SampleDirty); Assert.False(owner.StartPlanCommand.CanExecute(null)); await b.AssertNoIO(); vm.DiscardSampleCommand.Execute(null); Assert.False(vm.CreatingSample); Assert.False(vm.SampleDirty);
    }
    [Theory]
    [InlineData(SampleTopology.TwoWire, 2)]
    [InlineData(SampleTopology.FourWire, 4)]
    [InlineData(SampleTopology.HallBar, 6)]
    [InlineData(SampleTopology.VanDerPauw, 4)]
    public async Task TopologyChangeRequiresFreshMappingsAndIgnoresOldMappingCallbacks(SampleTopology topology, int count)
    {
        var b = new Bench(SampleTopology.HallBar); await using var owner = b.Shell; var calls = 0; var form = new PlanSampleCreationFormViewModel(b.Plan.Routing!, () => calls++); form.Topology = SampleTopology.HallBar; var stale = form.Mapping!;
        form.Topology = topology == SampleTopology.HallBar ? SampleTopology.FourWire : topology; if (topology == SampleTopology.HallBar) form.Topology = topology;
        Assert.Equal(count, form.Mapping!.Electrodes.Count); Assert.All(form.Mapping.Electrodes, e => { Assert.Null(e.SelectedChannel); Assert.Null(e.SelectedConnection); }); Assert.Null(form.Mapping.SelectedWiring);
        var before = calls; stale.Electrodes[0].Notes = "stale"; Assert.Equal(before, calls); form.IsReadOnly = true; Assert.True(form.Details.IsReadOnly); Assert.True(form.Mapping.IsReadOnly); Assert.All(form.Mapping.Electrodes, e => Assert.True(e.IsReadOnly));
    }
    [Theory]
    [InlineData(SampleTopology.TwoWire)]
    [InlineData(SampleTopology.FourWire)]
    [InlineData(SampleTopology.HallBar)]
    [InlineData(SampleTopology.VanDerPauw)]
    public async Task ExistingElectrodeRebindPreservesIdentityDetailsAndRunsFreshNewRoutes(SampleTopology topology)
    {
        var b = new Bench(topology); await using var owner = b.Shell; var vm = owner.Plans!; var old = vm.ValidatedPlan!; var original = old.Routing!.Samples[0];
        Map(vm.ElectrodeForm!); vm.SampleForm!.Material = "GaAs"; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var sample = vm.ValidatedPlan!.Routing!.Samples[0]; Assert.Equal(original.Id, sample.Id); Assert.Equal(original.Topology, sample.Topology); Assert.Equal("GaAs", sample.Material); Assert.Equal(original.Geometry, sample.Geometry); Assert.Same(old.Root, vm.ValidatedPlan.Root); Assert.All(original.Electrodes, e => Assert.Equal("channel1", e.Channel.Value));
        var path = Path.Combine(Path.GetTempPath(), "sample-electrodes-" + Guid.NewGuid().ToString("N") + ".json");
        try { await b.Workspace.SaveAsync(path, vm.DocumentText, CancellationToken.None); var fresh = new Bench(topology); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); Assert.All(Assert.Single(fresh.Runs.LatestDataSet!.Rows).Routing!.Roles, role => Assert.InRange(role.Point.Column, 7, 12)); await fresh.AssertCleanup(); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ExplicitIndependentRouteGuardRejectsIncoherentMappingReplacement()
    {
        var b = new Bench(SampleTopology.FourWire, true); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; Map(vm.ElectrodeForm!); vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); await b.AssertNoIO();
    }
    [Fact]
    public async Task SampleRemovalRejectsUsedOrLastSampleAndRemovesOnlyUnusedCatalogEntry()
    {
        var b = new Bench(SampleTopology.HallBar); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.RemoveSampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.SelectedSample = vm.SampleChoices.Single(choice => choice.Profile.Id.Value == "unused"); vm.RemoveSampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Single(vm.ValidatedPlan!.Routing!.Samples); Assert.False(vm.RemoveSampleCommand.CanExecute(null)); vm.RemoveSampleCommand.Execute(null); Assert.Single(vm.ValidatedPlan.Routing.Samples); await b.AssertNoIO();
    }
    [Fact]
    public async Task CreationAndMappingDraftsRespectBusyStaleSelectionAndOtherEditorAdmission()
    {
        var b = new Bench(SampleTopology.HallBar); await using var owner = b.Shell; var vm = owner.Plans!; var oldDetails = vm.SampleForm!; vm.CreateSampleCommand.Execute(null); var stale = vm.CreationForm!;
        Assert.True(oldDetails.IsReadOnly); Assert.True(vm.ElectrodeForm!.IsReadOnly); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.True(vm.NodeForm!.IsReadOnly); Assert.True(vm.PreparationForm!.IsReadOnly); var before = vm.DocumentText;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(stale.IsReadOnly); vm.ApplySampleCommand.Execute(null); Assert.Equal(before, vm.DocumentText); owner.IsBusy = false; vm.DiscardSampleCommand.Execute(null); stale.Details.Material = "late"; Assert.False(vm.SampleDirty);
        var oldMap = vm.ElectrodeForm!; Map(oldMap); vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); oldMap.Electrodes[0].Notes = "stale"; Assert.False(vm.SampleDirty);
        vm.NodeFragment += " "; Assert.False(vm.CreateSampleCommand.CanExecute(null)); Assert.True(vm.ElectrodeForm!.IsReadOnly); vm.DiscardNodeCommand.Execute(null); vm.PreparationForm!.LevelText = ".02"; Assert.True(vm.ElectrodeForm.IsReadOnly); vm.DiscardPreparationCommand.Execute(null); await b.AssertNoIO();
    }
    [Fact]
    public async Task ConnectionChoicesUseActualCatalogInsteadOfCallerMetadataAndRebindClearsPoint()
    {
        var b = new Bench(SampleTopology.HallBar); await using var owner = b.Shell; var row = b.Shell.Plans!.ElectrodeForm!.Electrodes[0]; var original = row.Build(); Assert.Equal("channel1", original.Channel.Value);
        var actual = row.Channels.Single(choice => choice.Profile.Id.Value == "channel2"); row.SelectedChannel = new(new(actual.Profile.Id, "forged", actual.Profile.Connections)); Assert.Null(row.SelectedConnection); var connection = row.Connections.Single(choice => choice.Connection.Key.Value == "c0"); row.SelectedConnection = new(new(connection.Connection.Key, 99, 999)); var built = row.Build(); Assert.Equal(actual.Profile.Id, built.Channel); Assert.Equal(connection.Connection.Key, built.Connection); Assert.Equal(7, actual.Profile.Connections.Single(c => c.Key == built.Connection).Column);
    }
    private static void Author(PlanSampleCreationFormViewModel form, SampleTopology topology)
    { form.SampleIdText = "created"; form.Details.SampleName = "New sample"; form.Topology = topology; Map(form.Mapping!); }
    private static void Map(PlanSampleElectrodesFormViewModel form)
    {
        form.SelectedWiring = form.Wirings[0];
        for (var i = 0; i < form.Electrodes.Count; i++) { var row = form.Electrodes[i]; row.SelectedChannel = row.Channels.Single(channel => channel.Profile.Id.Value == "channel2"); row.SelectedConnection = row.Connections.Single(connection => connection.Connection.Key.Value == "c" + i); }
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockKeithley6221CurrentSource Current { get; } = new(new("current"), "Current");
        public MockMatrixSwitch Matrix { get; }
        public ExperimentPlan Plan { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench(SampleTopology topology, bool independent = false, bool smuSource = false)
        {
            var labels = SampleProfile.RequiredLabels(topology); var ch1 = new ChannelProfile(new("channel1"), "CH1", Enumerable.Range(0, 6).Select(i => new ChannelConnection(new("c" + i), 1, i + 1)));
            var ch2 = new ChannelProfile(new("channel2"), "CH2", Enumerable.Range(0, 6).Select(i => new ChannelConnection(new("c" + i), 1, i + 7)).Append(new(new("outside"), 1, 99)));
            var sample = new SampleProfile(new("primary"), "Primary", topology, labels.Select((label, i) => new SampleElectrode(new(label), ch1.Id, new("c" + i))), new(new(.002, PhysicalUnit.Meter), new(.004, PhysicalUnit.Meter), new(.000001, PhysicalUnit.Meter)));
            var unused = new SampleProfile(new("unused"), "Unused", topology, sample.Electrodes, sample.Geometry); var meter = new MockKeithley2182Meter(new("meter"), "Meter"); var sourceId = independent && !smuSource ? Current.Descriptor.Id : Source.Id;
            var wiring = new InstrumentWiringProfile(new("wire"), sourceId, new(1, 1), new(1, 2), new(1, 3), new(1, 4), voltageMeter: independent ? meter.Descriptor.Id : null); var routing = new RoutingConfiguration(new("matrix"), [ch1, ch2], [wiring], [sample, unused]);
            var selection = topology switch { SampleTopology.TwoWire => SampleRouteSelection.TwoWireResistance, SampleTopology.FourWire => SampleRouteSelection.FourWireResistance, SampleTopology.HallBar => SampleRouteSelection.HallLongitudinal, _ => SampleRouteSelection.VdpAbCd }; var binding = new SampleRoutingBinding(sample.Id, wiring.Id, selection);
            ResistanceAcquisitionStep acquisition = independent ? new ReadCurrentResistanceStep(new("read"), sourceId, meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(1e-12, PhysicalUnit.Ampere), new ConstantValue(new(.00001, PhysicalUnit.Ampere)), new(routing.Matrix, routing.Resolve(binding).Closed)) : new ReadResistanceStep(new("read"), sourceId, SourceFunction.Voltage, new("V"), new("I"), new("R"), new(1e-12, PhysicalUnit.Ampere));
            Plan = new("catalog", new SequenceStep(new("root"), [new ReadSampleResistanceStep(acquisition, binding, TimeSpan.Zero)]), independent && !smuSource ? [] : [new(Source.Id, smuSource ? SourceFunction.Current : SourceFunction.Voltage, smuSource ? new(.00001, PhysicalUnit.Ampere) : new(.01, PhysicalUnit.Volt), smuSource ? new(10, PhysicalUnit.Volt) : new(.001, PhysicalUnit.Ampere), new(smuSource ? SenseFunction.Voltage : SenseFunction.Current, topology == SampleTopology.TwoWire ? SensingMode.TwoWire : SensingMode.FourWire, 1, true))], routing: routing,
                currentSourcePreparations: independent && !smuSource ? [new(Current.Descriptor.Id, new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))] : null, maximumStepExecutions: 100);
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 12)], [Source.Id, Current.Descriptor.Id]));
            Runs = new(new DeviceRegistry([manager, new MatrixSwitchSession(Matrix, hub), new Keithley6221CurrentSourceSession(Current, hub), new Keithley2182MeterSession(meter, hub)], new(TimeSpan.FromSeconds(5))), hub); Workspace = new(new JsonExperimentPlanDocument(), Runs); Shell = new(manager, Runs, hub, Workspace, Plan, "unused-catalog.json");
        }
        public async Task AssertNoIO() { Assert.False((await Current.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Matrix.GetSnapshotAsync(CancellationToken.None)).Connected); }
        public async Task AssertCleanup() { Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False((await Current.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.Empty((await Matrix.GetSnapshotAsync(CancellationToken.None)).Closed); Assert.False(Runs.HasActiveRun); }
    }
}

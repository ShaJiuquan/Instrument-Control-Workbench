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

public sealed class PlanContextBindingFormTests
{
    [Fact]
    public async Task EnvironmentCatalogContainsOnlyRegisteredActualAxisReadbacksAndIsReadOnlyWithoutIO()
    {
        var b = new EnvironmentBench(); await using var owner = b.Shell;
        Assert.Same(b.Registry.AxisDefinitions, b.Workspace.AxisDefinitions); Assert.Equal(3, b.Workspace.AxisDefinitions.Count);
        Assert.True(((IList<AxisControllerDefinition>)b.Workspace.AxisDefinitions).IsReadOnly);
        Assert.DoesNotContain(b.Workspace.AxisDefinitions, a => a.Device.Id == b.Source.InstrumentId);
        foreach (var axis in b.Workspace.AxisDefinitions)
        {
            Assert.Equal(ParameterAccess.Read, axis.Readback.Access); Assert.NotEqual(axis.Target.Id, axis.Readback.Id);
            Assert.False((await b.Registry.GetStatusAsync(axis.Device.Id, CancellationToken.None)).Connected);
        }
    }
    [Fact]
    public void UneditedEnvironmentPreservesAuthoredOrderAndMissingCatalogBindingsUntilAnExplicitBindingChange()
    {
        var reads = new EnvironmentRead[] { new(EnvironmentRole.Field, new("B"), new(new("legacy"), new("readback"))), new(EnvironmentRole.Temperature, new("T")) };
        var calls = 0; var form = new PlanEnvironmentFormViewModel(reads, [], () => calls++); Assert.Same(reads, form.Build()); Assert.Equal(0, calls);
        var field = form.Entries.Single(e => e.Role == EnvironmentRole.Field); field.Column = "magnet";
        Assert.Equal(new[] { EnvironmentRole.Field, EnvironmentRole.Temperature }, form.Build().Select(r => r.Role)); Assert.Equal(reads[0].Address, form.Build()[0].Address);
        field.SelectedController = null; // A null-to-null setter has no semantic change.
        field.BindController = false; Assert.Null(form.Build()[0].Address); field.BindController = true; Assert.Throws<InvalidOperationException>(() => form.Build());
    }
    [Theory]
    [InlineData("read")]
    [InlineData("sourceSense")]
    [InlineData("resistance")]
    public async Task EnvironmentBindingSavesReloadsAndProducesActualReadbacksAndExplicitMissingObservations(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "environment-form-" + Guid.NewGuid().ToString("N") + ".json"); var b = new EnvironmentBench(kind); await using var owner = b.Shell;
        var vm = owner.Plans!; var env = vm.NodeForm!.Environment!; var temp = env.Entries.Single(e => e.Role == EnvironmentRole.Temperature); var field = env.Entries.Single(e => e.Role == EnvironmentRole.Field);
        temp.IsIncluded = true; temp.Column = "actual_temperature"; temp.BindController = true; temp.SelectedController = temp.Choices.Single();
        field.IsIncluded = true; field.Column = "missing_field"; Assert.False(field.BindController);
        Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null)); vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        foreach (var device in b.Registry.Devices) Assert.False((await b.Registry.GetStatusAsync(device.Id, CancellationToken.None)).Connected);
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var fresh = new EnvironmentBench(kind); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows);
            var actual = Assert.Single(row.Environment, o => o.Column.Value == "actual_temperature"); Assert.NotNull(actual.Value); Assert.Equal(PhysicalUnit.Kelvin, actual.Value!.Value.Unit);
            Assert.Equal(new ParameterAddress(new("demo-temperature"), new("temperature.readback")), actual.Source);
            var missing = Assert.Single(row.Environment, o => o.Column.Value == "missing_field"); Assert.Null(missing.Value); Assert.Null(missing.Source); Assert.DoesNotContain(new ParameterId("missing_field"), row.Values.Keys);
            Assert.True((await fresh.Source.GetDeviceStatusAsync(CancellationToken.None)).SafetyConfirmed); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("measurementColumn")]
    [InlineData("elapsed")]
    [InlineData("missingController")]
    [InlineData("wrongRole")]
    public async Task InvalidEnvironmentBindingCannotReplaceThePlanOrConnectDevices(string damage)
    {
        var b = new EnvironmentBench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; var form = vm.NodeForm!.Environment!;
        var temp = form.Entries.Single(e => e.Role == EnvironmentRole.Temperature); temp.IsIncluded = true;
        if (damage == "duplicate") { var field = form.Entries.Single(e => e.Role == EnvironmentRole.Field); field.IsIncluded = true; field.Column = temp.Column; }
        else if (damage == "measurementColumn") temp.Column = "reading";
        else if (damage == "elapsed") temp.Column = "elapsed";
        else { temp.BindController = true; if (damage == "wrongRole") temp.SelectedController = form.Entries.Single(e => e.Role == EnvironmentRole.Field).Choices.Single(); }
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty);
        foreach (var device in b.Registry.Devices) Assert.False((await b.Registry.GetStatusAsync(device.Id, CancellationToken.None)).Connected);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.NodeForm!.Environment!.Entries.Single(e => e.Role == EnvironmentRole.Temperature).IsIncluded);
    }
    [Fact]
    public async Task NestedEnvironmentDraftHonorsStaleMixedBusyAndPreparationLocks()
    {
        var b = new EnvironmentBench(); await using var owner = b.Shell; var vm = owner.Plans!; var stale = vm.NodeForm!.Environment!.Entries[0];
        stale.IsIncluded = true; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var applied = vm.DocumentText;
        stale.Column = "stale"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.All(vm.NodeForm!.Environment!.Entries, e => { Assert.True(e.IsReadOnly); Assert.False(e.CanEdit); });
        vm.NodeForm.Environment.Entries[0].Column = "busy"; vm.ApplyNodeCommand.Execute(null); Assert.Equal(applied, vm.DocumentText); owner.IsBusy = false; vm.DiscardNodeCommand.Execute(null);
        vm.NodeForm!.Environment!.Entries[0].Column = "new"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(applied, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        vm.PreparationForm!.LevelText = ".02"; Assert.True(vm.NodeForm!.Environment!.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    [Theory]
    [InlineData(SampleTopology.TwoWire, 1)]
    [InlineData(SampleTopology.FourWire, 1)]
    [InlineData(SampleTopology.HallBar, 2)]
    [InlineData(SampleTopology.VanDerPauw, 8)]
    public void SampleTopologyChoicesUseActualCatalogRatherThanSelectionMetadata(SampleTopology topology, int count)
    {
        var fixture = Fixture(); var profile = fixture.Routing!.Samples.Single();
        var sample = new SampleProfile(profile.Id, profile.Name, topology, SampleProfile.RequiredLabels(topology).Select((label, i) => new SampleElectrode(new(label), new("channel1"), new("connection" + i))));
        var catalog = new RoutingConfiguration(fixture.Routing.Matrix, fixture.Routing.Channels, fixture.Routing.Wirings, [sample]);
        var form = new PlanSampleRoutingFormViewModel(((ReadSampleResistanceStep)((ScanStep)fixture.Root).Body).Binding, catalog, false, () => { });
        Assert.Equal(count, form.Measurements.Count);
        form.SelectedSample = new(new(profile.Id, "forged", SampleTopology.HallBar, SampleProfile.RequiredLabels(SampleTopology.HallBar).Select((label, i) => new SampleElectrode(new(label), new("channel1"), new("connection" + i))))); Assert.Null(form.SelectedMeasurement); Assert.Equal(count, form.Measurements.Count);
        form.IsReadOnly = true; Assert.False(form.CanRebind);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebindingSampleAndWiringSavesReloadsAndExecutesNewFrozenCrosspoints(bool independent)
    {
        var b = new RoutingBench(independent); await using var owner = b.Shell; var vm = owner.Plans!; var old = vm.ValidatedPlan!; var form = vm.NodeForm!.Acquisition!; var routing = form.SampleRouting!;
        routing.SelectedSample = routing.Samples.Single(s => s.Profile.Id.Value == "sample2"); Assert.Null(routing.SelectedMeasurement);
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(b.Codec.Serialize(old), vm.DocumentText);
        routing.SelectedMeasurement = SampleRouteSelection.FourWireResistance; routing.SelectedWiring = routing.Wirings.Single(w => w.Profile.Id.Value == "wiring2");
        if (independent) { Assert.Null(form.SelectedReference); Assert.Empty(form.ExpectedCurrent); form.SelectedReference = CurrentReference.ProgrammedSetpoint; form.ExpectedCurrent = ".00002"; }
        vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Equal(old.Metadata, vm.ValidatedPlan!.Metadata); Assert.Equal(old.MaximumStepExecutions, vm.ValidatedPlan.MaximumStepExecutions);
        Assert.False((await b.Matrix.GetSnapshotAsync(CancellationToken.None)).Connected);
        var path = Path.Combine(Path.GetTempPath(), "routing-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message);
            var loaded = await new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), b.Runs).LoadAsync(path, CancellationToken.None);
            var read = (ReadSampleResistanceStep)loaded.Plan.Root; var expected = loaded.Plan.Routing!.Resolve(read.Binding);
            Assert.Equal("sample2", read.Binding.Sample.Value); Assert.Equal("wiring2", read.Binding.Wiring.Value); Assert.Equal(5, expected.Closed.Min(p => p.Column));
            if (independent) { var acq = (ReadCurrentResistanceStep)read.Acquisition; Assert.Equal(new InstrumentId("current2"), acq.Source); Assert.Equal(new InstrumentId("meter2"), acq.Meter); Assert.Equal(new MatrixRouteExpectation(expected.Matrix, expected.Closed), acq.RequiredRoute); }
            await b.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(expected.Binding, row.Routing!.Binding); loaded.Plan.Routing.ValidateContext(row.Routing);
            Assert.Empty((await b.Matrix.GetSnapshotAsync(CancellationToken.None)).Closed); Assert.False(b.Runs.HasActiveRun);
            var participants = b.Runs.PreviewPlan(loaded.Plan).Devices;
            foreach (var device in b.Registry.Devices.Where(d => d.StopPolicy == DeviceStopPolicy.OutputOff))
            {
                var status = await b.Registry.GetStatusAsync(device.Id, CancellationToken.None);
                if (participants.Contains(device.Id)) Assert.True(status.SafetyConfirmed); else Assert.False(status.Connected);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("topology")]
    [InlineData("unknownSample")]
    [InlineData("unknownWiring")]
    [InlineData("disabledElectrode")]
    [InlineData("wrongArchitecture")]
    public async Task InvalidSampleBindingPreservesPlanAndEveryDeviceStaysDisconnected(string damage)
    {
        var b = new RoutingBench(false, damage == "disabledElectrode"); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; var routing = vm.NodeForm!.Acquisition!.SampleRouting!;
        routing.SelectedSample = routing.Samples.Single(s => s.Profile.Id.Value == "sample2"); routing.SelectedMeasurement = SampleRouteSelection.FourWireResistance;
        if (damage == "topology") routing.SelectedMeasurement = SampleRouteSelection.HallTransverse;
        else if (damage == "unknownSample") routing.SelectedSample = new(new(new("absent"), "absent", SampleTopology.FourWire, routing.Samples[0].Profile.Electrodes));
        else if (damage == "unknownWiring") routing.SelectedWiring = new(new(new("absent"), new("k2450"), new(1, 1), new(1, 2), new(1, 3), new(1, 4)));
        else if (damage == "wrongArchitecture") routing.SelectedWiring = new(new(new("independent-only"), new("current"), new(1, 1), new(1, 2), new(1, 3), new(1, 4), voltageMeter: new("meter")));
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty);
        foreach (var device in b.Registry.Devices) Assert.False((await b.Registry.GetStatusAsync(device.Id, CancellationToken.None)).Connected);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError);
    }
    private static ExperimentPlan Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples/plans/sample-four-wire.plan.json"))) directory = directory.Parent;
        return new JsonExperimentPlanDocument().Parse(File.ReadAllText(Path.Combine((directory ?? throw new IOException("Fixture missing.")).FullName, "examples/plans/sample-four-wire.plan.json")));
    }
    private sealed class EnvironmentBench
    {
        public DeviceRegistry Registry { get; }
        public InstrumentManager Source { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public EnvironmentBench(string kind = "read")
        {
            var config = DemoDeviceConfiguration.Create(); var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            var runtime = new ConfiguredDeviceRuntime(config, new ConfiguredDeviceFactory(), leases, hub); Registry = runtime.Registry; Source = runtime.PrimarySource; Runs = new(Registry, hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var source = Source.InstrumentId;
            ExperimentStep read = kind == "sourceSense" ? new ReadSourceSenseStep(new("read"), source, SourceFunction.Voltage, new("voltage"), new("current"))
                : kind == "resistance" ? new ReadResistanceStep(new("read"), source, SourceFunction.Voltage, new("voltage"), new("current"), new("resistance"), new(0, PhysicalUnit.Ampere))
                : new ReadStep(new("read"), [new(new("reading"), new(source, Keithley2450Parameters.MeasuredCurrent.Id))]);
            var plan = new ExperimentPlan("environment bindings", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), source, true), read]), [new(source, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]);
            Shell = new(Source, Runs, hub, Workspace, plan, "unused-context.json"); Shell.Plans!.GraphNodes.Single(n => n.Id.Value == "read").SelectCommand.Execute(null);
        }
    }
    private sealed class RoutingBench
    {
        public JsonExperimentPlanDocument Codec { get; } = new();
        public DeviceRegistry Registry { get; }
        public MockMatrixSwitch Matrix { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public RoutingBench(bool independent, bool disabled = false)
        {
            var fixture = Fixture(); var initial = (ReadSampleResistanceStep)((ScanStep)fixture.Root).Body; var catalog = fixture.Routing!; var wire = catalog.Wirings.Single(); var channel = catalog.Channels.Single(); var sample = catalog.Samples.Single();
            var channel2 = new ChannelProfile(new("channel2"), "Channel2", channel.Connections.Select(c => new ChannelConnection(c.Key, c.Slot, c.Column is { } col ? col + 4 : null, c.Enabled)));
            var sample2 = new SampleProfile(new("sample2"), "Second sample", SampleTopology.FourWire, sample.Electrodes.Select((e, i) => new SampleElectrode(e.Label, channel2.Id, e.Connection, !disabled || i != 0)));
            var wiring1 = independent ? new InstrumentWiringProfile(wire.Id, new("current"), wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus, voltageMeter: new("meter")) : wire;
            var wiring2 = new InstrumentWiringProfile(new("wiring2"), new(independent ? "current2" : "k2450"), wire.SourcePlus, wire.SourceMinus, wire.SensePlus, wire.SenseMinus, voltageMeter: independent ? new InstrumentId("meter2") : null);
            var routing = new RoutingConfiguration(catalog.Matrix, [channel, channel2], [wiring1, wiring2], [sample, sample2]);
            var hub = new NotificationHub(); var source = new MockKeithley2450SourceMeter(); var manager = new InstrumentManager(source, hub);
            var current = new MockKeithley6221CurrentSource(new("current"), "DC1"); var current2 = new MockKeithley6221CurrentSource(new("current2"), "DC2"); var meter = new MockKeithley2182Meter(new("meter"), "Meter1"); var meter2 = new MockKeithley2182Meter(new("meter2"), "Meter2");
            Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 10)], independent ? [current.Descriptor.Id, current2.Descriptor.Id] : [source.Id]));
            Registry = new([manager, new MatrixSwitchSession(Matrix, hub), new Keithley6221CurrentSourceSession(current, hub), new Keithley6221CurrentSourceSession(current2, hub), new Keithley2182MeterSession(meter, hub), new Keithley2182MeterSession(meter2, hub)], new(TimeSpan.FromSeconds(5)));
            Runs = new(Registry, hub); Workspace = new(Codec, Runs);
            ResistanceAcquisitionStep acquisition = independent ? new ReadCurrentResistanceStep(initial.Id, current.Descriptor.Id, meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint,
                new("V"), new("I"), new("R"), new(1e-12, PhysicalUnit.Ampere), new ConstantValue(new(.00001, PhysicalUnit.Ampere)), new(routing.Matrix, routing.Resolve(initial.Binding).Closed)) : initial.Acquisition;
            var plan = new ExperimentPlan("routing bindings", new ReadSampleResistanceStep(acquisition, initial.Binding, TimeSpan.Zero), fixture.Preparations,
                new Dictionary<string, Quantity> { ["fixture"] = new(1, PhysicalUnit.Dimensionless) }, routing,
                independent ? [new(current.Descriptor.Id, new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt))), new(current2.Descriptor.Id, new(new(.00002, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))] : null, 80);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-routing.json");
        }
    }
}

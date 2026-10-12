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
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanSampleAnalysisFormTests
{
    [Theory]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task AnalysisRoundtripRetainsOrderedReadingsDistinctSettingsAndFrozenKinds(SampleTopology topology, bool independent)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "analysis").SelectCommand.Execute(null);
        Assert.True(vm.SupportsNodeForm); Assert.False(vm.NodeFragmentDirty); var before = vm.DocumentText; var built = (SampleAnalysisStep)vm.NodeForm!.Build();
        Assert.Equal(b.Analysis.Id, built.Id); Assert.Equal(b.Analysis.Columns, built.Columns); Assert.Equal(b.Analysis.Readings.Select(r => r.Id), built.Readings.Select(r => r.Id));
        for (var i = 0; i < built.Readings.Count; i++)
        {
            var old = b.Analysis.Readings[i]; var value = built.Readings[i]; Assert.Equal(old.Binding, value.Binding); Assert.Equal(old.Settle, value.Settle); Assert.Equal(old.AcquisitionSettle, value.AcquisitionSettle); Assert.Equal(old.Acquisition.MinimumCurrent, value.Acquisition.MinimumCurrent); Assert.Equal(old.Acquisition.EnvironmentReads, value.Acquisition.EnvironmentReads);
            if (old.Acquisition is ReadCurrentResistanceStep prior) { var current = (ReadCurrentResistanceStep)value.Acquisition; Assert.Equal(prior.ExpectedProgrammedCurrent, current.ExpectedProgrammedCurrent); Assert.Equal(prior.RequiredRoute, current.RequiredRoute); Assert.Equal(prior.Retry, current.Retry); Assert.Equal(prior.CurrentReference, current.CurrentReference); }
        }
        Assert.Equal(SampleResistanceAnalysis.Kinds(topology), vm.NodeForm.AnalysisForm!.Columns.Select(c => c.Kind)); Assert.Contains("Meter", vm.NodeForm.AnalysisForm.GeometrySummary);
        vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(vm.DocumentText)));
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Theory]
    [InlineData("rawCollision")]
    [InlineData("duplicateDerived")]
    [InlineData("missingDerived")]
    [InlineData("singleRawColumn")]
    [InlineData("singleEnvironment")]
    [InlineData("direction")]
    [InlineData("singleSample")]
    [InlineData("singleWiring")]
    [InlineData("externalRouting")]
    [InlineData("negativeThreshold")]
    public async Task InvalidWholeGroupCandidateCannotReplacePlanOrSendDeviceCommands(string damage)
    {
        var b = new Bench(SampleTopology.HallBar, false); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "analysis").SelectCommand.Execute(null); var before = vm.DocumentText; var form = vm.NodeForm!.AnalysisForm!;
        switch (damage)
        {
            case "rawCollision": form.Columns[0].ColumnId = "voltage"; break;
            case "duplicateDerived": form.Columns[0].ColumnId = form.Columns[1].ColumnId; break;
            case "missingDerived": form.Columns[0].ColumnId = ""; break;
            case "singleRawColumn": form.Readings[0].Acquisition.VoltageColumn = "other_voltage"; break;
            case "singleEnvironment": form.Readings[0].Environment.Entries[0].Column = "other_temp"; break;
            case "direction": form.Readings[1].Acquisition.SampleRouting!.SelectedMeasurement = SampleRouteSelection.HallLongitudinal; break;
            case "singleSample":
                var binding = form.Readings[0].Acquisition.SampleRouting!; var selection = binding.SelectedMeasurement; binding.SelectedSample = binding.Samples.Single(s => s.Profile.Id.Value == "sample2"); binding.SelectedMeasurement = selection; break;
            case "singleWiring":
                var wire = form.Readings[0].Acquisition.SampleRouting!; wire.SelectedWiring = wire.Wirings.Single(w => w.Profile.Id.Value == "wire2"); break;
            case "externalRouting": form.Readings[0].Acquisition.RoutingManagedExternally = true; break;
            default: form.Readings[0].Acquisition.MinimumCurrent = "-1"; break;
        }
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(CancellationToken.None)).Connected); vm.DiscardNodeCommand.Execute(null); Assert.False(vm.HasError);
    }
    [Theory]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task CoordinatedSampleRebindAndRenamedColumnsSaveReloadAndAnalyzeFreshActualRows(SampleTopology topology, bool independent)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "analysis").SelectCommand.Execute(null); var form = vm.NodeForm!.AnalysisForm!;
        foreach (var column in form.Columns) column.ColumnId = "edited_" + column.Kind;
        foreach (var reading in form.Readings)
        {
            var routing = reading.Acquisition.SampleRouting!; var selection = routing.SelectedMeasurement; routing.SelectedSample = routing.Samples.Single(s => s.Profile.Id.Value == "sample2"); routing.SelectedWiring = routing.Wirings.Single(w => w.Profile.Id.Value == "wire2"); routing.SelectedMeasurement = selection;
            reading.Acquisition.VoltageColumn = "edited_voltage"; reading.Acquisition.CurrentColumn = "edited_current"; reading.Acquisition.ResistanceColumn = "edited_resistance";
        }
        Assert.Contains("Sample 2", form.GeometrySummary); vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var path = Path.Combine(Path.GetTempPath(), "analysis-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(topology, independent); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); var analysis = (SampleAnalysisStep)((SequenceStep)loaded.Plan.Root).Children[0];
            Assert.All(analysis.Readings, r => { Assert.Equal("sample2", r.Binding.Sample.Value); Assert.Equal("wire2", r.Binding.Wiring.Value); if (r.Acquisition is ReadCurrentResistanceStep current) Assert.Equal(new MatrixRouteExpectation(loaded.Plan.Routing!.Matrix, loaded.Plan.Routing.Resolve(r.Binding).Closed), current.RequiredRoute); });
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); await VerifyRows(fresh, analysis, loaded.Plan.Routing!, "edited_resistance");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(SampleTopology.HallBar, false, "Hall 分析")]
    [InlineData(SampleTopology.VanDerPauw, false, "范德堡分析")]
    [InlineData(SampleTopology.HallBar, true, "独立源 Hall 分析")]
    [InlineData(SampleTopology.VanDerPauw, true, "独立源范德堡分析")]
    public async Task PaletteRequiresExplicitColumnsThresholdAndRoutingThenSavesReloadsAndRuns(SampleTopology topology, bool independent, string kind)
    {
        var b = new Bench(topology, independent); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = kind; var form = vm.PaletteForm!.AnalysisForm!; var before = vm.DocumentText;
        Assert.All(form.Columns, c => Assert.Empty(c.ColumnId)); Assert.All(form.Readings, r => Assert.Empty(r.Acquisition.MinimumCurrent)); vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        foreach (var column in form.Columns) column.ColumnId = "new_" + column.Kind;
        var selections = SampleResistanceAnalysis.Selections(topology);
        for (var i = 0; i < form.Readings.Count; i++)
        {
            var acq = form.Readings[i].Acquisition; acq.MinimumCurrent = "1e-12"; var route = acq.SampleRouting!;
            route.SelectedSample = route.Samples.Single(s => s.Profile.Id.Value == "sample1"); route.SelectedWiring = route.Wirings.Single(w => w.Profile.Id.Value == "wire1"); route.SelectedMeasurement = selections[i];
            if (independent) acq.SelectedReference = CurrentReference.ProgrammedSetpoint;
        }
        vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False((await b.Matrix.GetSnapshotAsync(CancellationToken.None)).Connected);
        var added = (SampleAnalysisStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[^1]; Assert.Equal(selections, added.Readings.Select(r => r.Binding.Selection));
        // Isolate the created analysis using the same validated envelope and a fresh runtime.
        var created = new ExperimentPlan(vm.ValidatedPlan.Name, new SequenceStep(new("created-root"), [added]), vm.ValidatedPlan.Preparations, vm.ValidatedPlan.Metadata, vm.ValidatedPlan.Routing, vm.ValidatedPlan.CurrentSourcePreparations, vm.ValidatedPlan.MaximumStepExecutions);
        var path = Path.Combine(Path.GetTempPath(), "analysis-palette-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await b.Workspace.SaveAsync(path, new JsonExperimentPlanDocument().Serialize(created), CancellationToken.None); var fresh = new Bench(topology, independent); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); await VerifyRows(fresh, (SampleAnalysisStep)((SequenceStep)loaded.Plan.Root).Children[0], loaded.Plan.Routing!, added.Readings[0].Acquisition.ResistanceColumn.Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task MissingGeometryKeepsRawRowsAndReportsUnavailableDerivedValues()
    {
        var b = new Bench(SampleTopology.HallBar, false, missingGeometry: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "analysis").SelectCommand.Execute(null);
        Assert.Contains("未提供", vm.NodeForm!.AnalysisForm!.GeometrySummary); vm.NodeForm.AnalysisForm.Columns[0].ColumnId = "actual_xx"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); await b.Runs.StartPlanAsync(vm.ValidatedPlan!, CancellationToken.None);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); Assert.True(rows[^1].Analysis!.Issues.HasFlag(SampleAnalysisIssues.MissingGeometry)); Assert.True(rows[^1].Values.ContainsKey(new("actual_xx"))); Assert.False(rows[^1].Values.ContainsKey(new("SheetResistance"))); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }
    [Fact]
    public async Task AnalysisNestedDraftsRetainReadonlyBusyStaleMixedAndPreparationGuards()
    {
        var b = new Bench(SampleTopology.HallBar, false); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "analysis").SelectCommand.Execute(null); var stale = vm.NodeForm!.AnalysisForm!;
        stale.Columns[0].ColumnId = "changed_xx"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var before = vm.DocumentText; stale.Columns[0].ColumnId = "old_draft"; Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); var form = vm.NodeForm!.AnalysisForm!; Assert.All(form.Columns, c => Assert.True(c.IsReadOnly)); Assert.All(form.Readings, r => { Assert.True(r.Acquisition.IsReadOnly); Assert.True(r.Environment.IsReadOnly); Assert.True(r.Acquisition.SampleRouting!.IsReadOnly); }); vm.ApplyNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText);
        owner.IsBusy = false; form.Readings[0].Acquisition.MinimumCurrent = "1e-12"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        vm.PreparationForm!.LevelText = ".01"; Assert.True(vm.NodeForm!.AnalysisForm!.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    private static async Task VerifyRows(Bench b, SampleAnalysisStep analysis, RoutingConfiguration routing, string resistanceColumn)
    {
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(analysis.Readings.Count + 1, rows.Count); Assert.All(rows.Take(analysis.Readings.Count), row => { Assert.NotNull(row.Routing); Assert.Null(row.Analysis); Assert.True(row.Values.ContainsKey(new(resistanceColumn))); });
        Assert.Equal(analysis.Readings.Select(r => r.Binding.Selection), rows.Take(analysis.Readings.Count).Select(r => r.Routing!.Binding.Selection));
        var sample = analysis.ValidateRouting(routing); var expected = SampleResistanceAnalysis.Calculate(sample.Topology, sample.Geometry, rows.Take(analysis.Readings.Count).Select(r => r.Values[new(resistanceColumn)]).ToArray());
        var summary = rows[^1]; Assert.NotNull(summary.Analysis); Assert.Equal(expected.Issues, summary.Analysis.Issues); foreach (var column in analysis.Columns) Assert.Equal(expected.Values[column.Kind], summary.Values[column.Column]);
        Assert.False(summary.Values.ContainsKey(new(resistanceColumn))); Assert.False(b.Runs.HasActiveRun); Assert.Empty((await b.Matrix.GetSnapshotAsync(CancellationToken.None)).Closed);
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False((await b.Current.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockKeithley6221CurrentSource Current { get; } = new(new("current"), "Current");
        public MockMatrixSwitch Matrix { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public SampleAnalysisStep Analysis { get; }
        public Bench(SampleTopology topology, bool independent, bool missingGeometry = false)
        {
            var labels = SampleProfile.RequiredLabels(topology); var channel1 = new ChannelProfile(new("channel1"), "CH1", labels.Select((_, i) => new ChannelConnection(new("c" + i), 1, i + 1)));
            var channel2 = new ChannelProfile(new("channel2"), "CH2", labels.Select((_, i) => new ChannelConnection(new("c" + i), 1, i + 7)));
            var geometry = missingGeometry ? SampleGeometry.Empty : new SampleGeometry(new(.002, PhysicalUnit.Meter), new(.004, PhysicalUnit.Meter), new(.000001, PhysicalUnit.Meter));
            var samples = new[] { channel1, channel2 }.Select((ch, i) => new SampleProfile(new("sample" + (i + 1)), "Sample " + (i + 1), topology, labels.Select((label, index) => new SampleElectrode(new(label), ch.Id, new("c" + index))), geometry)).ToArray();
            var meter = new MockKeithley2182Meter(new("meter"), "Meter"); var sourceId = independent ? Current.Descriptor.Id : Source.Id;
            var wirings = new[] { "wire1", "wire2" }.Select(id => new InstrumentWiringProfile(new(id), sourceId, new(1, 1), new(1, 2), new(1, 3), new(1, 4), voltageMeter: independent ? meter.Descriptor.Id : null)).ToArray();
            var routing = new RoutingConfiguration(new("matrix"), [channel1, channel2], wirings, samples); var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub);
            Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 16)], [Source.Id, Current.Descriptor.Id]));
            var registry = new DeviceRegistry([manager, new MatrixSwitchSession(Matrix, hub), new Keithley6221CurrentSourceSession(Current, hub), new Keithley2182MeterSession(meter, hub)], new(TimeSpan.FromSeconds(5))); Runs = new(registry, hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var readings = SampleResistanceAnalysis.Selections(topology).Select((selection, i) =>
            {
                var id = new StepId("read" + i); var binding = new SampleRoutingBinding(samples[0].Id, wirings[0].Id, selection); var env = new[] { new EnvironmentRead(EnvironmentRole.Temperature, new("temperature")) };
                ResistanceAcquisitionStep acquisition = independent ? new ReadCurrentResistanceStep(id, sourceId, meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint, new("voltage"), new("current"), new("resistance"), new((i + 1) * 1e-15, PhysicalUnit.Ampere), new ConstantValue(new(.00001, PhysicalUnit.Ampere)), new(routing.Matrix, routing.Resolve(binding).Closed), env, new(true, TimeSpan.Zero, false))
                    : new ReadResistanceStep(id, sourceId, SourceFunction.Voltage, new("voltage"), new("current"), new("resistance"), new((i + 1) * 1e-15, PhysicalUnit.Ampere), env);
                return new ReadSampleResistanceStep(acquisition, binding, TimeSpan.FromMilliseconds(i), acquisitionSettle: TimeSpan.FromMilliseconds(i));
            });
            Analysis = new(new("analysis"), readings, SampleResistanceAnalysis.Kinds(topology).Select(kind => new SampleDerivedColumn(kind, new(kind.ToString()))));
            var plan = new ExperimentPlan("analysis form", new SequenceStep(new("root"), [Analysis]), independent ? [] : [new(Source.Id, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))], routing: routing,
                currentSourcePreparations: independent ? [new(Current.Descriptor.Id, new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))] : null, maximumStepExecutions: 100);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-analysis.json");
        }
    }
}

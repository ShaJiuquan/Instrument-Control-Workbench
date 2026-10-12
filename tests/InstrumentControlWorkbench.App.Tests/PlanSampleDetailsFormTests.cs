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
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanSampleDetailsFormTests
{
    [Fact]
    public void OptionalDimensionsPreserveNullsAndExplicitMeterValuesWithoutDirtyInitialization()
    {
        var sample = Fixture().Routing!.Samples.Single(); var calls = 0; var form = new PlanSampleDetailsFormViewModel(sample, () => calls++);
        Assert.Equal(0, calls); Assert.Equal(sample.Geometry, form.Build().Geometry); Assert.False(form.Spacing.Included); Assert.False(form.Spacing.CanEditValue);
        form.Spacing.Included = true; Assert.Throws<FormatException>(() => form.Build()); form.Spacing.ValueText = "0.001"; Assert.Equal(new Quantity(.001, PhysicalUnit.Meter), form.Build().Geometry.Spacing);
        form.Width.Included = false; form.Width.ValueText = "NaN"; Assert.Null(form.Build().Geometry.Width); form.Width.Included = true; Assert.Throws<FormatException>(() => form.Build());
        form.IsReadOnly = true; Assert.All(form.Dimensions, d => { Assert.True(d.IsReadOnly); Assert.False(d.CanEditValue); }); Assert.True(calls > 0);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("")]
    [InlineData("1,2")]
    [InlineData("0.1x")]
    [InlineData("1e-9999")]
    public async Task InvalidProvidedGeometryRetainsPlanAndLocksRunUntilDiscard(string text)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; vm.SampleForm!.Width.ValueText = text;
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.SampleDirty); Assert.True(vm.HasUnappliedEdits); Assert.False(owner.StartPlanCommand.CanExecute(null));
        Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(CancellationToken.None)).Connected); vm.DiscardSampleCommand.Execute(null); Assert.False(vm.HasUnappliedEdits); Assert.False(vm.HasError);
    }
    [Theory]
    [InlineData("name")]
    [InlineData("longName")]
    [InlineData("material")]
    public async Task InvalidSampleMetadataCannotApply(string damage)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        if (damage == "name") vm.SampleForm!.SampleName = ""; else if (damage == "longName") vm.SampleForm!.SampleName = new string('x', 1025); else vm.SampleForm!.Material = new string('x', 8193);
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.SampleDirty); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Fact]
    public async Task SampleDraftLocksFilesNodePreparationSelectionAndJsonWithoutSendingIO()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; var selected = vm.SelectedSample; var node = vm.SelectedNode;
        vm.SampleForm!.Width.ValueText = ".004"; Assert.True(vm.SampleDirty); Assert.True(vm.NodeForm!.IsReadOnly); Assert.True(vm.PreparationForm!.IsReadOnly); Assert.False(vm.ValidateCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.False(vm.LoadCommand.CanExecute(null));
        vm.SelectedSample = vm.SampleChoices.Single(s => s.Profile.Id.Value == "second"); Assert.Equal(selected, vm.SelectedSample); vm.GraphNodes[^1].SelectCommand.Execute(null); Assert.Equal(node, vm.SelectedNode);
        vm.DocumentText += " "; Assert.Equal(before, vm.DocumentText); vm.ValidateCommand.Execute(null); vm.ApplyNodeCommand.Execute(null); vm.ApplyPreparationCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.DiscardSampleCommand.Execute(null); Assert.False(vm.SampleDirty); Assert.False(vm.HasUnappliedEdits); Assert.True(vm.SaveCommand.CanExecute(null));
        vm.NodeFragment += " "; Assert.True(vm.SampleForm.IsReadOnly); vm.DiscardNodeCommand.Execute(null); vm.PreparationForm.LevelText = ".02"; Assert.True(vm.SampleForm.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    [Fact]
    public async Task SampleFormsKeepStaleBusyDocumentBusyAndForgedSelectionGuards()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var selected = vm.SelectedSample!;
        var forged = new SampleProfile(selected.Profile.Id, "forged", selected.Profile.Topology, selected.Profile.Electrodes); vm.SelectedSample = new(forged); Assert.Equal(selected, vm.SelectedSample);
        var stale = vm.SampleForm!; stale.Width.ValueText = ".004"; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var applied = vm.DocumentText; stale.Width.ValueText = "-1"; Assert.False(vm.SampleDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.SampleForm!.IsReadOnly); vm.ApplySampleCommand.Execute(null); vm.SelectedSample = vm.SampleChoices[^1]; Assert.Equal(applied, vm.DocumentText); Assert.Equal(selected.Profile.Id, vm.SelectedSample!.Profile.Id);
        owner.IsBusy = false; vm.IsDocumentBusy = true; Assert.True(vm.SampleForm.IsReadOnly); vm.IsDocumentBusy = false; Assert.False(vm.SampleForm.IsReadOnly); vm.SelectedSample = vm.SampleChoices.Single(s => s.Profile.Id.Value == "second"); Assert.Equal("second", vm.SampleForm.Id.Value);
        stale.Material = "late"; Assert.False(vm.SampleDirty); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateLoadOrSaveResultCannotDiscardANewSampleDraft(bool save)
    {
        var delayed = new DelayedDocument(Fixture()); var b = new Bench(documents: delayed); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        var pending = save ? vm.SaveCommand.ExecuteAsync(null) : vm.LoadCommand.ExecuteAsync(null); await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(vm.IsDocumentBusy); Assert.True(vm.SampleForm!.IsReadOnly);
        vm.SampleForm.Width.ValueText = ".004"; delayed.Release.SetResult(); await pending; Assert.True(vm.HasError); Assert.True(vm.SampleDirty); Assert.Equal(before, vm.DocumentText); Assert.Equal(".004", vm.SampleForm.Width.ValueText); Assert.False(vm.IsDocumentBusy); Assert.False(owner.StartPlanCommand.CanExecute(null));
        if (save) Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(new JsonExperimentPlanDocument().Serialize(delayed.Saved!))));
        vm.DiscardSampleCommand.Execute(null); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Fact]
    public async Task RenamedSampleAndGeometrySaveFreshReloadChangeAnalysisButKeepRoutesAndCleanup()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; var original = vm.ValidatedPlan!; var oldSample = original.Routing!.Samples[0];
        var form = vm.SampleForm!; form.SampleName = "Edited Hall"; form.OriginalId = "wafer-02"; form.Material = "GaAs"; form.Substrate = "Si"; form.Notes = "line1\nline2"; form.Width.ValueText = ".004"; form.Thickness.ValueText = "2e-6"; form.Spacing.Included = true; form.Spacing.ValueText = ".001";
        vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(vm.IsDirty); Assert.False(vm.HasUnappliedEdits); Assert.Equal(oldSample.Geometry, original.Routing.Samples[0].Geometry); Assert.Same(original.Root, vm.ValidatedPlan!.Root);
        vm.GraphNodes.Single(n => n.Id.Value == "hall_analysis").SelectCommand.Execute(null); Assert.Contains("Edited Hall", vm.NodeForm!.AnalysisForm!.GeometrySummary); Assert.Contains("0.004 Meter", vm.NodeForm.AnalysisForm.GeometrySummary);
        var path = Path.Combine(Path.GetTempPath(), "sample-details-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            var sample = loaded.Plan.Routing!.Samples[0]; Assert.Equal("Edited Hall", sample.Name); Assert.Equal("GaAs", sample.Material); Assert.Equal("Si", sample.Substrate); Assert.Equal("wafer-02", sample.OriginalId); Assert.Equal("line1\nline2", sample.Notes); Assert.Equal(new Quantity(.001, PhysicalUnit.Meter), sample.Geometry.Spacing);
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var rows = fresh.Runs.LatestDataSet!.Rows; Assert.Equal(9, rows.Count); var analysis = (SampleAnalysisStep)((ScanStep)loaded.Plan.Root).Body;
            for (var i = 0; i < 3; i++)
            {
                var raw = rows.Skip(i * 3).Take(2).ToArray(); Assert.All(raw, row => { Assert.Equal("Edited Hall", row.Routing!.SampleName); loaded.Plan.Routing.ValidateContext(row.Routing); });
                var expected = SampleResistanceAnalysis.Calculate(sample.Topology, sample.Geometry, raw.Select(row => row.Values[new("resistance")]).ToArray()); var summary = rows[i * 3 + 2]; Assert.Equal(expected.Issues, summary.Analysis!.Issues); foreach (var column in analysis.Columns) Assert.Equal(expected.Values[column.Kind], summary.Values[column.Column]);
            }
            Assert.Equal(oldSample.Electrodes, sample.Electrodes); await fresh.AssertCleanup();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task NewGeometryAndMissingThicknessNeverRewriteEarlierArchivedAnalysis()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sample-geometry-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); RunId oldId; RunId newId; RunDataSet oldData;
        try
        {
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive: archive); await using var owner = b.Shell; var vm = owner.Plans!; await b.Runs.StartPlanAsync(vm.ValidatedPlan!, CancellationToken.None); oldData = b.Runs.LatestDataSet!; oldId = oldData.Id;
                vm.SampleForm!.Width.ValueText = ".004"; vm.SampleForm.Thickness.ValueText = "2e-6"; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); await b.Runs.StartPlanAsync(vm.ValidatedPlan!, CancellationToken.None); var changed = b.Runs.LatestDataSet!; newId = changed.Id;
                var oldGeometry = oldData.Plan!.Routing!.Samples[0].Geometry; var newGeometry = changed.Plan!.Routing!.Samples[0].Geometry;
                var sheetFactor = newGeometry.Width!.Value.Value / oldGeometry.Width!.Value.Value * oldGeometry.Length!.Value.Value / newGeometry.Length!.Value.Value;
                var resistivityFactor = sheetFactor * newGeometry.Thickness!.Value.Value / oldGeometry.Thickness!.Value.Value;
                for (var i = 2; i < 9; i += 3) { Assert.Equal(oldData.Rows[i].Values[new("r_sheet")].Value * sheetFactor, changed.Rows[i].Values[new("r_sheet")].Value, 8); Assert.Equal(oldData.Rows[i].Values[new("rho_xx")].Value * resistivityFactor, changed.Rows[i].Values[new("rho_xx")].Value, 8); }
                Assert.Equal(new Quantity(.002, PhysicalUnit.Meter), oldData.Plan!.Routing!.Samples[0].Geometry.Width);
                vm.SampleForm!.Thickness.Included = false; vm.ApplySampleCommand.Execute(null); await b.Runs.StartPlanAsync(vm.ValidatedPlan!, CancellationToken.None); var missing = b.Runs.LatestDataSet!; Assert.All(missing.Rows.Where(row => row.Analysis is not null), row => { Assert.True(row.Analysis!.Issues.HasFlag(SampleAnalysisIssues.MissingGeometry)); Assert.True(row.Values.ContainsKey(new("r_sheet"))); Assert.False(row.Values.ContainsKey(new("rho_xx"))); }); await b.AssertCleanup();
            }
            await using (var reopened = new FileRunArchive(folder))
            {
                var savedOld = await reopened.LoadAsync(oldId, CancellationToken.None); var savedNew = await reopened.LoadAsync(newId, CancellationToken.None);
                Assert.Equal(new Quantity(.002, PhysicalUnit.Meter), savedOld.DataSet.Plan!.Routing!.Samples[0].Geometry.Width); Assert.Equal(new Quantity(.004, PhysicalUnit.Meter), savedNew.DataSet.Plan!.Routing!.Samples[0].Geometry.Width); Assert.Equal(oldData.Rows[2].Values, savedOld.DataSet.Rows[2].Values);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    private static ExperimentPlan Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples/plans/sample-hall-analysis.plan.json"))) directory = directory.Parent;
        return new JsonExperimentPlanDocument().Parse(File.ReadAllText(Path.Combine((directory ?? throw new IOException("Fixture missing")).FullName, "examples/plans/sample-hall-analysis.plan.json")));
    }
    private sealed class DelayedDocument(ExperimentPlan loaded) : IExperimentPlanDocument
    {
        private readonly JsonExperimentPlanDocument codec = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExperimentPlan? Saved { get; private set; }
        public string Serialize(ExperimentPlan plan) => codec.Serialize(plan);
        public ExperimentPlan Parse(string text) => codec.Parse(text);
        public async Task<ExperimentPlan> LoadAsync(string path, CancellationToken token) { Entered.SetResult(); await Release.Task.WaitAsync(token); return loaded; }
        public async Task SaveAsync(string path, ExperimentPlan plan, CancellationToken token) { Entered.SetResult(); await Release.Task.WaitAsync(token); Saved = plan; }
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockMatrixSwitch Matrix { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench(IExperimentPlanDocument? documents = null, FileRunArchive? archive = null)
        {
            var fixture = Fixture(); var original = fixture.Routing!.Samples[0]; var second = new SampleProfile(new("second"), "Second sample", original.Topology, original.Electrodes, original.Geometry);
            var routing = new RoutingConfiguration(fixture.Routing.Matrix, fixture.Routing.Channels, fixture.Routing.Wirings, [original, second]); var plan = new ExperimentPlan(fixture.Name, fixture.Root, fixture.Preparations, fixture.Metadata, routing, fixture.CurrentSourcePreparations, fixture.MaximumStepExecutions);
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 12)], [Source.Id]));
            Runs = new(new DeviceRegistry([manager, new MatrixSwitchSession(Matrix, hub)], new(TimeSpan.FromSeconds(5))), hub, archive is null ? null : [archive]); Workspace = new(documents ?? new JsonExperimentPlanDocument(), Runs); Shell = new(manager, Runs, hub, Workspace, plan, "unused-sample-details.json");
        }
        public async Task AssertCleanup() { Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.Empty((await Matrix.GetSnapshotAsync(CancellationToken.None)).Closed); Assert.False(Runs.HasActiveRun); }
    }
}

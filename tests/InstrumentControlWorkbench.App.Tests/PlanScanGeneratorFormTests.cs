using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PlanScanGeneratorFormTests
{
    [Theory]
    [InlineData(PlanScanValueMode.List)]
    [InlineData(PlanScanValueMode.Linear)]
    [InlineData(PlanScanValueMode.Stepped)]
    [InlineData(PlanScanValueMode.Logarithmic)]
    public void FourModesUseDomainRulesAndPreserveExplicitListDuplicates(PlanScanValueMode mode)
    {
        var changes = 0; var form = Values(() => changes++); Assert.Equal(0, changes);
        var range = form.Definition; Configure(range, mode, "1", "3", "3", "0.75", "1,1,3"); var values = form.Build(); Assert.Equal(PhysicalUnit.Volt, values.Unit);
        var expected = mode switch
        {
            PlanScanValueMode.List => new[] { 1d, 1, 3 },
            PlanScanValueMode.Linear => [1d, 2, 3],
            PlanScanValueMode.Stepped => [1d, 1.75, 2.5, 3],
            _ => [1d, Math.Sqrt(3), 3],
        };
        Assert.Equal(expected.Length, values.Values.Count); for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], values.Values[i].Value, 12);
        form.PreviewCommand.Execute(null); Assert.Contains(values.Values.Count + " 点", form.PreviewText); Assert.Contains("Volt", form.PreviewText);
        range.EndText = "4"; Assert.Empty(form.PreviewText); Assert.True(changes > 0);
    }
    [Theory]
    [InlineData(PlanScanValueMode.Linear)]
    [InlineData(PlanScanValueMode.Stepped)]
    [InlineData(PlanScanValueMode.Logarithmic)]
    public void DescendingGeneratorsIncludeEndpoint(PlanScanValueMode mode)
    {
        var form = Values(); Configure(form.Definition, mode, "3", "1", "3", "0.75"); var result = form.Build(); Assert.Equal(3, result.Values[0].Value); Assert.Equal(1, result.Values[^1].Value);
        Assert.True(result.Values.Zip(result.Values.Skip(1), (a, b) => a.Value > b.Value).All(x => x));
    }
    [Theory]
    [InlineData(PlanScanValueMode.Linear, "NaN", "1", "3", "1")]
    [InlineData(PlanScanValueMode.Linear, "0", "Infinity", "3", "1")]
    [InlineData(PlanScanValueMode.Linear, "0", "1", "1", "1")]
    [InlineData(PlanScanValueMode.Linear, "0", "1", "1000001", "1")]
    [InlineData(PlanScanValueMode.Linear, "0", "1", "2.5", "1")]
    [InlineData(PlanScanValueMode.Linear, "0", "1", "", "1")]
    [InlineData(PlanScanValueMode.Stepped, "0", "1", "3", "0")]
    [InlineData(PlanScanValueMode.Stepped, "0", "1", "3", "-1")]
    [InlineData(PlanScanValueMode.Stepped, "0", "1", "3", "1e-12")]
    [InlineData(PlanScanValueMode.Logarithmic, "0", "1", "3", "1")]
    [InlineData(PlanScanValueMode.Logarithmic, "-1", "1", "3", "1")]
    [InlineData(PlanScanValueMode.Linear, "-1e308", "1e308", "3", "1")]
    public void InvalidDefinitionsRejectAndPreviewReportsError(PlanScanValueMode mode, string start, string end, string points, string step)
    {
        var form = Values(); Configure(form.Definition, mode, start, end, points, step);
        Assert.ThrowsAny<Exception>(() => form.Build()); form.PreviewCommand.Execute(null); Assert.NotEmpty(form.PreviewText); Assert.DoesNotContain(" 点 ·", form.PreviewText);
    }
    [Fact]
    public void SegmentsKeepOrderAndOnlyCollapseAdjacentDuplicatesWithReadonlyMembershipCommands()
    {
        var form = Values(); form.UseSegments = true; Assert.ThrowsAny<ArgumentException>(() => form.Build());
        form.AddSegmentCommand.Execute(null); form.AddSegmentCommand.Execute(null);
        Configure(form.Segments[0], PlanScanValueMode.Linear, "1", "3", "3", ""); Configure(form.Segments[1], PlanScanValueMode.List, "", "", "", "", "3,3,2,1");
        Assert.Equal(new[] { 1d, 2, 3, 2, 1 }, form.Build().Values.Select(q => q.Value));
        var first = form.Segments[0]; form.IsReadOnly = true; Assert.True(first.IsReadOnly); Assert.True(form.Definition.IsReadOnly); form.AddSegmentCommand.Execute(null); form.RemoveSegmentCommand.Execute(first); form.PreviewCommand.Execute(null); Assert.Equal(2, form.Segments.Count); Assert.Empty(form.PreviewText);
        form.IsReadOnly = false; form.RemoveSegmentCommand.Execute(first); form.RemoveSegmentCommand.Execute(first); Assert.Single(form.Segments);
    }
    [Fact]
    public void ContextChangesClearAllVisibleAndHiddenPhysicalInputsAndPreview()
    {
        var unit = PhysicalUnit.Volt; var form = new PlanScanValuesFormViewModel(new([new(1, unit)]), () => unit, _ => { }, () => { });
        Configure(form.Definition, PlanScanValueMode.Linear, "1", "3", "3", "1", "1,2"); form.AddSegmentCommand.Execute(null); Configure(form.Segments[0], PlanScanValueMode.Linear, "1", "2", "2", "1", "1");
        form.PreviewCommand.Execute(null); unit = PhysicalUnit.Ampere; form.ContextChanged(); Assert.Empty(form.PreviewText); Assert.Contains("Ampere", form.UnitLabel);
        foreach (var range in new[] { form.Definition, form.Segments[0] }) { Assert.Empty(range.ValuesText); Assert.Empty(range.StartText); Assert.Empty(range.EndText); Assert.Empty(range.PointsText); Assert.Empty(range.StepText); }
        Assert.Throws<FormatException>(() => form.Build());
    }
    [Theory]
    [InlineData("range")]
    [InlineData("budget")]
    [InlineData("segments")]
    public async Task InvalidGeneratedCandidateRetainsPlanAndCannotConnectOrStart(string damage)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null); var before = vm.DocumentText; var form = vm.NodeForm!.ScanValuesForm!;
        Configure(form.Definition, PlanScanValueMode.Linear, ".01", damage == "range" ? "1000" : ".03", damage == "budget" ? "100" : "3", ""); if (damage == "segments") form.UseSegments = true;
        vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
        vm.DiscardNodeCommand.Execute(null); Assert.False(vm.NodeFragmentDirty);
    }
    [Theory]
    [InlineData("linear")]
    [InlineData("stepped")]
    [InlineData("logarithmic")]
    [InlineData("segmented")]
    public async Task GeneratedSourceScanSavesExpandedValuesReloadsAndRunsWithOriginalBodyAndOutputOff(string kind)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null); var form = vm.NodeForm!.ScanValuesForm!;
        Configure(form.Definition, kind == "stepped" ? PlanScanValueMode.Stepped : kind == "logarithmic" ? PlanScanValueMode.Logarithmic : PlanScanValueMode.Linear, ".01", ".03", "3", ".015");
        if (kind == "segmented")
        {
            form.UseSegments = true; form.AddSegmentCommand.Execute(null); form.AddSegmentCommand.Execute(null);
            Configure(form.Segments[0], PlanScanValueMode.Linear, ".01", ".03", "3", ""); Configure(form.Segments[1], PlanScanValueMode.Linear, ".03", ".01", "3", "");
        }
        form.PreviewCommand.Execute(null); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected); var expected = form.Build(); vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var scan = (ScanStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[1]; Assert.Equal(new StepId("read"), scan.Body.Id); Assert.Equal(TimeSpan.Zero, scan.Dwell);
        var path = Path.Combine(Path.GetTempPath(), "generator-form-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text))); var reloaded = (ScanStep)((SequenceStep)loaded.Plan.Root).Children[1]; Assert.Equal(expected.Values, reloaded.Values.Values);
            var freshForm = new PlanStepFormViewModel(reloaded, fresh.Workspace.Devices, () => { }); Assert.False(freshForm.ScanValuesForm!.UseSegments); Assert.Equal(PlanScanValueMode.List, freshForm.ScanValuesForm.Definition.Mode); Assert.Equal(expected.Values, ((ScanStep)freshForm.Build()).Values.Values);
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var rows = fresh.Runs.LatestDataSet!.Rows; Assert.Equal(expected.Values.Count, rows.Count);
            for (var i = 0; i < rows.Count; i++) { Assert.Equal(expected.Values[i], rows[i].Values[new("voltage")]); Assert.Equal(expected.Values[i], rows[i].Values[new("bias")]); }
            Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(IterationTiming.Immediate)]
    [InlineData(IterationTiming.DelayBeforePoint)]
    public async Task GeneratedIterationSavesReloadsThreeRowsWithTimingUnitsIndexAndCleanup(IterationTiming timing)
    {
        var b = new Bench(iterator: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "iterator").SelectCommand.Execute(null); var form = vm.NodeForm!.IterationForm!;
        form.Timing = timing; form.IndexBase = IterationIndexBase.ZeroBased; Configure(form.ValuesForm.Definition, PlanScanValueMode.Linear, "0", timing == IterationTiming.Immediate ? "2" : ".002", "3", ""); vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var path = Path.Combine(Path.GetTempPath(), "iteration-generator-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            vm.DocumentPath = path; await vm.SaveCommand.ExecuteAsync(null); var fresh = new Bench(iterator: true); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None);
            var rows = fresh.Runs.LatestDataSet!.Rows; Assert.Equal(3, rows.Count); Assert.Equal(new[] { 0d, 1, 2 }, rows.Select(r => r.Values[new("item.index")].Value));
            Assert.All(rows, r => { Assert.Equal(3, r.Values[new("item.total")].Value); Assert.Equal(timing == IterationTiming.Immediate ? PhysicalUnit.Dimensionless : PhysicalUnit.Second, r.Values[new("item")].Unit); });
            Assert.False((await fresh.Source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled); Assert.False(fresh.Runs.HasActiveRun);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void IterationTimingAndUnitClearGeneratorsAndRejectNegativeDelayPreview()
    {
        var form = new PlanIterationFormViewModel(new(new("loop"), new("item"), new([new(1, PhysicalUnit.Dimensionless)]), new SequenceStep(new("body"), [])), () => { });
        Configure(form.ValuesForm.Definition, PlanScanValueMode.Linear, "0", "2", "3", ""); form.ValuesForm.PreviewCommand.Execute(null); form.Unit = PhysicalUnit.Volt; Assert.Empty(form.ValuesForm.Definition.StartText); Assert.Empty(form.ValuesForm.PreviewText);
        form.Timing = IterationTiming.DelayBeforePoint; Configure(form.ValuesForm.Definition, PlanScanValueMode.Linear, "-1", "0", "2", ""); form.ValuesForm.PreviewCommand.Execute(null); Assert.DoesNotContain(" 点 ·", form.ValuesForm.PreviewText); Assert.ThrowsAny<ArgumentException>(() => form.Build());
        form.IsReadOnly = true; Assert.True(form.ValuesForm.IsReadOnly); Assert.True(form.ValuesForm.Definition.IsReadOnly);
    }
    [Fact]
    public async Task GeneratedNestedDraftsPreserveStaleMixedBusyAndPreparationAdmission()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null); var stale = vm.NodeForm!.ScanValuesForm!;
        Configure(stale.Definition, PlanScanValueMode.Linear, ".01", ".03", "3", ""); vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var before = vm.DocumentText;
        stale.Definition.EndText = "1000"; Assert.False(vm.NodeFragmentDirty); var current = vm.NodeForm!.ScanValuesForm!; current.PreviewCommand.Execute(null); Assert.False(vm.NodeFragmentDirty);
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(current.IsReadOnly); current.AddSegmentCommand.Execute(null); Assert.Empty(current.Segments); vm.ApplyNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText);
        owner.IsBusy = false; current.Definition.ValuesText = ".02,.03"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        vm.PreparationForm!.LevelText = ".02"; Assert.True(vm.NodeForm!.ScanValuesForm!.IsReadOnly); vm.DiscardPreparationCommand.Execute(null);
    }
    [Fact]
    public async Task HardwareParameterRebindClearsAllGeneratorDraftsAndUsesActualCatalogUnit()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "scan").SelectCommand.Execute(null);
        var node = vm.NodeForm!; var form = node.ScanValuesForm!; Configure(form.Definition, PlanScanValueMode.Linear, ".01", ".03", "3", "");
        form.UseSegments = true; form.AddSegmentCommand.Execute(null); Configure(form.Segments[0], PlanScanValueMode.Linear, ".01", ".03", "3", ""); form.PreviewCommand.Execute(null);
        node.Address.SelectedParameter = node.Address.Parameters.Single(p => p.Descriptor.Id == Keithley2450Parameters.SourceCurrent.Id);
        Assert.Contains("Ampere", form.UnitLabel); Assert.Empty(form.PreviewText); Assert.Empty(form.Definition.StartText); Assert.Empty(form.Segments[0].StartText);
        Assert.Throws<FormatException>(() => node.Build()); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    [Theory]
    [InlineData("参数扫描")]
    [InlineData("数值迭代")]
    public async Task GeneratedPaletteRequiresAuthoredInputsAndCreatesExplicitValuesWithoutConnection(string kind)
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; vm.PaletteKind = kind; var node = vm.PaletteForm!;
        PlanScanValuesFormViewModel form;
        if (kind == "参数扫描")
        {
            node.Address.SelectedDevice = node.Address.Devices.Single(d => d.Descriptor.Id == b.Source.Id);
            node.Address.SelectedParameter = node.Address.Parameters.Single(p => p.Descriptor.Id == Keithley2450Parameters.SourceVoltage.Id);
            node.Column = "extra_bias"; form = node.ScanValuesForm!;
        }
        else { node.IterationForm!.Variable = "extra_item"; form = node.IterationForm.ValuesForm; }
        form.Definition.Mode = PlanScanValueMode.Linear; var before = vm.DocumentText; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        Configure(form.Definition, PlanScanValueMode.Linear, ".01", ".03", "3", ""); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var added = ((SequenceStep)vm.ValidatedPlan!.Root).Children[^1]; var values = added is ScanStep scan ? scan.Values : ((ForEachStep)added).Values;
        Assert.Equal(3, values.Values.Count); Assert.Equal(kind == "参数扫描" ? PhysicalUnit.Volt : PhysicalUnit.Dimensionless, values.Unit); Assert.False((await b.Source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }
    private static PlanScanValuesFormViewModel Values(Action? changed = null) => new(new([new(1, PhysicalUnit.Volt)]), () => PhysicalUnit.Volt, _ => { }, changed ?? (() => { }));
    private static void Configure(PlanScanRangeFormViewModel range, PlanScanValueMode mode, string start, string end, string points, string step, string list = "")
    { range.Mode = mode; range.StartText = start; range.EndText = end; range.PointsText = points; range.StepText = step; range.ValuesText = list; }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public Bench(bool iterator = false)
        {
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Runs = new(manager, new(TimeSpan.FromSeconds(5)), hub); Workspace = new(new JsonExperimentPlanDocument(), Runs);
            var read = new ReadStep(new("read"), [new(new("voltage"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id))]);
            ExperimentStep container = iterator ? new ForEachStep(new("iterator"), new("item"), new([new(1, PhysicalUnit.Dimensionless)]), read)
                : new ScanStep(new("scan"), new(Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("bias"), new([new(.01, PhysicalUnit.Volt)]), TimeSpan.Zero, read);
            var plan = new ExperimentPlan("generator", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), Source.Id, true), container]),
                [new(Source.Id, SourceFunction.Voltage, new(.01, PhysicalUnit.Volt), new(.0001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))], maximumStepExecutions: 100);
            Shell = new(manager, Runs, hub, Workspace, plan, "unused-generator.json");
        }
    }
}

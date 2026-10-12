using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanSampleCatalogFormTests
{
    [Theory]
    [InlineData(SampleTopology.FourWire, false)]
    [InlineData(SampleTopology.HallBar, false)]
    [InlineData(SampleTopology.VanDerPauw, false)]
    [InlineData(SampleTopology.FourWire, true)]
    [InlineData(SampleTopology.HallBar, true)]
    [InlineData(SampleTopology.VanDerPauw, true)]
    public async Task ExplicitGuardCoordinationSavesFreshReloadAndExecutesBothIndependentSourceFamilies(SampleTopology topology, bool smuSource)
    {
        var b = new Bench(topology, independent: true, smuSource); await using var owner = b.Shell; var vm = owner.Plans!; var original = vm.ValidatedPlan!; var originalRead = (ReadSampleResistanceStep)((SequenceStep)original.Root).Children[0];
        Assert.True(vm.ElectrodeForm!.AllowGuardCoordination); Assert.False(vm.ElectrodeForm.CoordinateIndependentRoutes); Map(vm.ElectrodeForm); vm.ElectrodeForm.CoordinateIndependentRoutes = true; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False(vm.HasUnappliedEdits);
        var updatedRead = (ReadSampleResistanceStep)((SequenceStep)vm.ValidatedPlan!.Root).Children[0]; var oldAcquisition = (ReadCurrentResistanceStep)originalRead.Acquisition; var updatedAcquisition = (ReadCurrentResistanceStep)updatedRead.Acquisition;
        Assert.Equal(originalRead.Id, updatedRead.Id); Assert.Equal(originalRead.Binding, updatedRead.Binding); Assert.Equal(originalRead.Settle, updatedRead.Settle); Assert.Equal(originalRead.AcquisitionSettle, updatedRead.AcquisitionSettle); Assert.Equal(oldAcquisition.Source, updatedAcquisition.Source); Assert.Equal(oldAcquisition.Meter, updatedAcquisition.Meter); Assert.Equal(oldAcquisition.CurrentReference, updatedAcquisition.CurrentReference); Assert.Equal(oldAcquisition.ExpectedProgrammedCurrent, updatedAcquisition.ExpectedProgrammedCurrent); Assert.Equal(oldAcquisition.MinimumCurrent, updatedAcquisition.MinimumCurrent); Assert.Equal(oldAcquisition.Retry, updatedAcquisition.Retry);
        Assert.NotEqual(oldAcquisition.RequiredRoute, updatedAcquisition.RequiredRoute); Assert.All(oldAcquisition.RequiredRoute!.Closed, point => Assert.InRange(point.Column, 1, 6));
        var path = Path.Combine(Path.GetTempPath(), "coordinated-guards-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await b.Workspace.SaveAsync(path, vm.DocumentText, CancellationToken.None); var fresh = new Bench(topology, independent: true, smuSource); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(vm.DocumentText), JsonNode.Parse(loaded.Text)));
            await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var row = Assert.Single(fresh.Runs.LatestDataSet!.Rows); Assert.All(row.Routing!.Roles, role => Assert.InRange(role.Point.Column, 7, 12)); Assert.True(row.Values.ContainsKey(new("R"))); await fresh.AssertCleanup();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(SampleTopology.HallBar, 9)]
    [InlineData(SampleTopology.VanDerPauw, 27)]
    public async Task NestedRepeatedAnalysisCoordinatesOnlySelectedSampleAndKeepsEveryOtherField(SampleTopology topology, int rowsExpected)
    {
        var b = new Bench(topology, independent: true); await using var owner = b.Shell; var vm = owner.Plans!; var codec = new JsonExperimentPlanDocument(); var raw = (ReadSampleResistanceStep)((SequenceStep)b.Plan.Root).Children[0]; var acq = (ReadCurrentResistanceStep)raw.Acquisition;
        SampleAnalysisStep Group(string sampleId, string prefix)
        {
            var readings = SampleResistanceAnalysis.Selections(topology).Select((selection, i) =>
            {
                var binding = new SampleRoutingBinding(new(sampleId), raw.Binding.Wiring, selection); var route = b.Plan.Routing!.Resolve(binding);
                var acquisition = new ReadCurrentResistanceStep(new(prefix + "_read_" + i), acq.Source, acq.Meter, acq.CurrentReference, acq.VoltageColumn, acq.CurrentColumn, acq.ResistanceColumn, new((i + 1) * 1e-12, PhysicalUnit.Ampere), acq.ExpectedProgrammedCurrent, new(route.Matrix, route.Closed), acq.EnvironmentReads, new(true, TimeSpan.FromMilliseconds(i), true));
                return new ReadSampleResistanceStep(acquisition, binding, TimeSpan.FromMilliseconds(i), acquisitionSettle: TimeSpan.FromMilliseconds(i));
            });
            return new(new(prefix + "_analysis"), readings, SampleResistanceAnalysis.Kinds(topology).Select(kind => new SampleDerivedColumn(kind, new(prefix + "_" + kind))));
        }
        var primary = Group("primary", "main"); var other = Group("unused", "other");
        var unrelated = new ReadCurrentResistanceStep(new("standalone"), acq.Source, acq.Meter, acq.CurrentReference, acq.VoltageColumn, acq.CurrentColumn, acq.ResistanceColumn, acq.MinimumCurrent, acq.ExpectedProgrammedCurrent, acq.RequiredRoute);
        var root = new SequenceStep(new("nested_root"), [new AssignValueStep(new("flag"), new("never"), new BooleanValue(false)),
            new RepeatStep(new("repeat"), 2, primary), new SubtreeStep(new("scope"), [new SequenceStep(new("inside"), [other])]),
            new IfElseStep(new("inactive"), new(new("never"), ComparisonOperator.Equal, new BooleanValue(true)), new SequenceStep(new("external_steps"), [unrelated, new VerifyMatrixRouteStep(new("external_verify"), acq.RequiredRoute!.Device, acq.RequiredRoute.Closed), new RouteMatrixStep(new("external_route"), acq.RequiredRoute.Device, acq.RequiredRoute.Closed, TimeSpan.Zero)]), new SequenceStep(new("empty"), []))]);
        var plan = new ExperimentPlan("nested guards", root, b.Plan.Preparations, b.Plan.Metadata, b.Plan.Routing, b.Plan.CurrentSourcePreparations, 100);
        vm.DocumentText = codec.Serialize(plan); vm.ValidateCommand.Execute(null); Assert.False(vm.HasError, vm.Message); var before = vm.DocumentText;
        Map(vm.ElectrodeForm!); vm.ElectrodeForm!.CoordinateIndependentRoutes = true; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message);
        var updated = vm.ValidatedPlan!; var ids = primary.Readings.Concat(other.Readings).Select(read => read.Id).Append(unrelated.Id).ToArray(); var afterSteps = codec.ReadSteps(updated, ids);
        foreach (var read in afterSteps.OfType<ReadSampleResistanceStep>())
        {
            var acquisition = (ReadCurrentResistanceStep)read.Acquisition; var route = updated.Routing!.Resolve(read.Binding); Assert.Equal(new MatrixRouteExpectation(route.Matrix, route.Closed), acquisition.RequiredRoute);
            Assert.All(route.Closed, point => Assert.InRange(point.Column, read.Binding.Sample.Value == "primary" ? 7 : 1, read.Binding.Sample.Value == "primary" ? 12 : 6));
        }
        Assert.Equal(unrelated.RequiredRoute, ((ReadCurrentResistanceStep)afterSteps[^1]).RequiredRoute);
        var oldJson = JsonNode.Parse(before)!; var newJson = JsonNode.Parse(vm.DocumentText)!; StripExpectedEdits(oldJson); StripExpectedEdits(newJson); Assert.True(JsonNode.DeepEquals(oldJson, newJson));
        var path = Path.Combine(Path.GetTempPath(), "nested-guards-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await b.Workspace.SaveAsync(path, vm.DocumentText, CancellationToken.None); var fresh = new Bench(topology, independent: true); await using var freshOwner = fresh.Shell; var loaded = await fresh.Workspace.LoadAsync(path, CancellationToken.None); await fresh.Runs.StartPlanAsync(loaded.Plan, CancellationToken.None); var rows = fresh.Runs.LatestDataSet!.Rows; Assert.Equal(rowsExpected, rows.Count); Assert.Equal(3, rows.Count(row => row.Analysis is not null));
            Assert.All(rows.Where(row => row.Routing?.Binding.Sample.Value == "primary"), row => Assert.All(row.Routing!.Roles, role => Assert.InRange(role.Point.Column, 7, 12))); Assert.All(rows.Where(row => row.Routing?.Binding.Sample.Value == "unused"), row => Assert.All(row.Routing!.Roles, role => Assert.InRange(role.Point.Column, 1, 6))); await fresh.AssertCleanup();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("outsideCard")]
    [InlineData("duplicateTerminal")]
    [InlineData("missingWiring")]
    public async Task CoordinationDoesNotBypassInvalidMappingAdmission(string damage)
    {
        var b = new Bench(SampleTopology.HallBar, independent: true); await using var owner = b.Shell; var vm = owner.Plans!; var before = vm.DocumentText; Map(vm.ElectrodeForm!); var form = vm.ElectrodeForm!; form.CoordinateIndependentRoutes = true;
        if (damage == "outsideCard") form.Electrodes[^1].SelectedConnection = form.Electrodes[^1].Connections.Single(choice => choice.Connection.Key.Value == "outside"); else if (damage == "duplicateTerminal") form.Electrodes[1].SelectedConnection = form.Electrodes[0].SelectedConnection; else form.SelectedWiring = null;
        vm.ApplySampleCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.SampleDirty); await b.AssertNoIO();
    }
    [Fact]
    public async Task CoordinationCheckboxRespectsReadonlyAndStaleDraftAdmission()
    {
        var b = new Bench(SampleTopology.FourWire, independent: true); await using var owner = b.Shell; var vm = owner.Plans!; var stale = vm.ElectrodeForm!; Map(stale); stale.CoordinateIndependentRoutes = true; var before = vm.DocumentText;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.False(stale.CanCoordinateGuards); vm.ApplySampleCommand.Execute(null); Assert.Equal(before, vm.DocumentText); owner.IsBusy = false; vm.ApplySampleCommand.Execute(null); Assert.False(vm.HasError, vm.Message); stale.CoordinateIndependentRoutes = false; Assert.False(vm.SampleDirty); Assert.False(vm.ElectrodeForm!.CoordinateIndependentRoutes); await b.AssertNoIO();
    }
    private static void StripExpectedEdits(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["kind"]?.GetValue<string>() == "read_independent_sample_resistance" && obj["binding"]?["sample"]?.GetValue<string>() == "primary") ((JsonObject)obj["acquisition"]!).Remove("requiredRoute");
            if (obj["id"]?.GetValue<string>() == "primary" && obj.ContainsKey("electrodes")) obj.Remove("electrodes");
            foreach (var child in obj.Select(pair => pair.Value).Where(child => child is not null).ToArray()) StripExpectedEdits(child!);
        }
        else if (node is JsonArray array) foreach (var child in array.Where(child => child is not null)) StripExpectedEdits(child!);
    }
}

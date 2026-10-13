using System.Text.Json.Nodes;
using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PlanSr830AcWorkflowTests
{
    [Fact]
    public async Task ExistingBindingRoundTripApplyDiscardAndStaleCallbacksPreserveUneditedRecipe()
    {
        var b = new Bench(withRead: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "ac").SelectCommand.Execute(null);
        var before = vm.DocumentText; var original = Read(b); var form = vm.NodeForm!.Sr830Ac!; Assert.True(vm.NodeForm.HasSr830Ac); Assert.False(vm.NodeFragmentDirty);
        var codec = new JsonExperimentPlanDocument(); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(codec.Serialize(new("same", original))), JsonNode.Parse(codec.Serialize(new("same", vm.NodeForm.Build())))));
        Assert.Equal(new[] { Second, First }, form.Connections); form.SineAmplitude = ".02"; Assert.True(vm.NodeFragmentDirty); Assert.False(owner.StartPlanCommand.CanExecute(null));
        vm.DiscardNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.False(vm.NodeFragmentDirty); form.Frequency = "18"; Assert.False(vm.NodeFragmentDirty);
        var appliedForm = vm.NodeForm!.Sr830Ac!; appliedForm.SineAmplitude = ".02"; vm.ApplyNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.False(vm.NodeFragmentDirty);
        var edited = Assert.IsType<ReadSr830AcFrameStep>(Assert.Single(Assert.IsType<SequenceStep>(codec.Parse(vm.DocumentText).Root).Children));
        Assert.Equal(original.Id, edited.Id); Assert.Equal(original.Device, edited.Device); Assert.Equal(original.Isolator, edited.Isolator); Assert.Equal(original.Connections, edited.Connections); Assert.Equal(original.Settle, edited.Settle); Assert.Equal(original.Input, edited.Input); Assert.Equal(original.Excitation.Frequency, edited.Excitation.Frequency); Assert.Equal(new(.02, PhysicalUnit.Volt), edited.Excitation.SineAmplitude);
        appliedForm.Frequency = "19"; Assert.False(vm.NodeFragmentDirty); Assert.Equal(0, b.IoCalls);
    }
    [Fact]
    public async Task TemplateRequiresExplicitGeneratorIsolationAndInputBeforeAppending()
    {
        var b = new Bench(); await using var owner = b.Shell; var vm = owner.Plans!; Assert.Contains(Palette, vm.PaletteKinds); vm.PaletteKind = Palette;
        var form = vm.PaletteForm!.Sr830Ac!; Assert.Null(form.SelectedGenerator); Assert.Null(form.SelectedIsolator); Assert.Null(form.SelectedInput); Assert.Empty(form.Prefix);
        var before = vm.DocumentText; vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        form.SelectedGenerator = Assert.Single(form.Generators); Assert.Null(form.SelectedIsolator); form.SelectedInput = Sr830InputMode.VoltageAB; form.Prefix = "chosen";
        vm.AppendNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        form.SelectedIsolator = Assert.Single(form.Isolators); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.True(vm.IsValidated);
        var step = Assert.IsType<ReadSr830AcFrameStep>(Assert.Single(Assert.IsType<SequenceStep>(new JsonExperimentPlanDocument().Parse(vm.DocumentText).Root).Children)); Assert.Equal(new[] { First, Second }, step.Connections); Assert.Equal(0, b.IoCalls);
    }
    [Fact]
    public async Task WholePlanPreflightRejectsPartialFrameColumnCollisionWithoutChangingTheSavedDocument()
    {
        var b = new Bench(withRead: true, occupiedColumn: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "ac").SelectCommand.Execute(null); var before = vm.DocumentText;
        vm.NodeForm!.Sr830Ac!.Prefix = "occupied"; Assert.IsType<ReadSr830AcFrameStep>(vm.NodeForm.Build()); vm.ApplyNodeCommand.Execute(null);
        Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.NodeFragmentDirty); Assert.False(vm.IsValidated); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.False(vm.SaveCommand.CanExecute(null));
        vm.DiscardNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); Assert.False(vm.NodeFragmentDirty); Assert.True(vm.IsValidated); Assert.Equal(0, b.IoCalls);
    }
    [Fact]
    public async Task BusyAndActualRunLocksAndMixedJsonDraftsCannotMutateTheValidatedDocument()
    {
        var b = new Bench(withRead: true); await using var owner = b.Shell; var vm = owner.Plans!; vm.GraphNodes.Single(n => n.Id.Value == "ac").SelectCommand.Execute(null); vm.PaletteKind = Palette; Fill(vm.PaletteForm!.Sr830Ac!); var before = vm.DocumentText;
        owner.IsBusy = true; vm.RefreshCommands(); Assert.True(vm.NodeForm!.Sr830Ac!.IsReadOnly); Assert.True(vm.PaletteForm.Sr830Ac!.IsReadOnly); Assert.False(vm.ApplyNodeCommand.CanExecute(null)); Assert.False(vm.AppendNodeCommand.CanExecute(null));
        vm.ApplyNodeCommand.Execute(null); vm.AppendNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); owner.IsBusy = false; vm.RefreshCommands(); Assert.Equal(0, b.IoCalls);
        vm.NodeForm.Sr830Ac.Frequency = "18"; vm.NodeFragment += " "; vm.ApplyNodeCommand.Execute(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); vm.DiscardNodeCommand.Execute(null);
        b.Generator.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously); var running = b.Runs.StartPlanAsync(new JsonExperimentPlanDocument().Parse(vm.DocumentText), Token);
        await b.Generator.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); vm.RefreshCommands(); Assert.True(vm.NodeForm!.Sr830Ac!.IsReadOnly); Assert.True(vm.PaletteForm!.Sr830Ac!.IsReadOnly); Assert.False(vm.ApplyNodeCommand.CanExecute(null)); Assert.False(vm.AppendNodeCommand.CanExecute(null)); Assert.False(owner.StartPlanCommand.CanExecute(null));
        vm.ApplyNodeCommand.Execute(null); vm.AppendNodeCommand.Execute(null); Assert.Equal(before, vm.DocumentText); b.Generator.ReadGate.SetResult(); await running.WaitAsync(TimeSpan.FromSeconds(5)); b.AssertReleased();
    }
    [Fact]
    public async Task ApplicationCapabilityCatalogExcludesPassiveReferenceProfilesWithoutDeviceIo()
    {
        var b = new Bench(mixedCatalog: true); await using var owner = b.Shell; Assert.Equal(b.Matrix.Definition, Assert.Single(b.Workspace.Sr830AcIsolationDefinitions));
        owner.Plans!.PaletteKind = Palette; var form = owner.Plans.PaletteForm!.Sr830Ac!; Assert.Equal(b.Generator.Descriptor.Id, Assert.Single(form.Generators).Descriptor.Id); Assert.Equal(b.Matrix.Definition, Assert.Single(form.Isolators).Definition); Assert.Null(form.SelectedGenerator); Assert.Equal(0, b.IoCalls);
    }
    [Fact]
    public async Task MismatchedRegisteredDisconnectSetCannotSilentlyRewriteOriginalConnections()
    {
        var b = new Bench(); await using var owner = b.Shell; var original = Read(b, [First]);
        var outer = new PlanStepFormViewModel(original, b.Workspace.Devices, () => { }, sr830AcIsolations: b.Workspace.Sr830AcIsolationDefinitions); var form = outer.Sr830Ac!;
        Assert.Equal(new[] { First }, form.Connections); Assert.ThrowsAny<Exception>(() => outer.Build()); form.Frequency = "18"; Assert.ThrowsAny<Exception>(() => outer.Build()); Assert.Equal(new[] { First }, form.Connections); Assert.Equal(new[] { First }, original.Connections); Assert.Equal(0, b.IoCalls);
    }
    [Fact]
    public async Task FormAuthoredThreeAcquisitionsSaveReloadRunAndExportFreshIsolationEvidence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sr830-active-form-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder); var author = new Bench(withRepeat: true); await using var authorShell = author.Shell; var vm = authorShell.Plans!;
            vm.GraphNodes.Single(n => n.Id.Value == "repeat-body").SelectCommand.Execute(null); vm.PaletteKind = Palette; Fill(vm.PaletteForm!.Sr830Ac!); vm.AppendNodeCommand.Execute(null); Assert.False(vm.HasError, vm.Message); Assert.Equal(0, author.IoCalls);
            var path = Path.Combine(folder, "plan.json"); await author.Workspace.SaveAsync(path, vm.DocumentText, Token); var loaded = await author.Workspace.LoadAsync(path, Token); Assert.Equal(0, author.IoCalls);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var replay = new Bench(listener: archive); await using var replayShell = replay.Shell; await replay.Runs.StartPlanAsync(loaded.Plan, Token); var data = replay.Runs.LatestDataSet!; Assert.Equal(3, data.Rows.Count); replay.AssertReleased();
            Assert.All(data.Rows, row =>
            {
                Assert.Equal(13, row.Values.Count); Assert.Equal(4, row.Quality.Count); Assert.Equal(new(.01, PhysicalUnit.Volt), row.Values[new("ui_ac.sine_amplitude_v")]); Assert.Equal(new(17, PhysicalUnit.Hertz), row.Values[new("ui_ac.reference_frequency_hz")]); Assert.Equal(.0023, row.Values[new("ui_ac.r_v")].Value);
                var proof = Assert.Single(row.Sr830Ac); Assert.Equal(replay.Generator.Descriptor.Id, proof.Device); Assert.Equal(replay.Matrix.Definition.Device.Id, proof.Isolator); Assert.DoesNotContain(First, proof.AfterAcquisitionOpen.Closed); Assert.DoesNotContain(Second, proof.AfterAcquisitionOpen.Closed);
            });
            Assert.Equal(new[] { Unrelated }, (await replay.Matrix.GetSnapshotAsync(Token)).Closed);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var restored = await fresh.LoadAsync(data.Id, Token); Assert.Equal(3, restored.DataSet.Rows.Count); Assert.NotNull(restored.DataSet.Blackboard);
            for (var index = 0; index < data.Rows.Count; index++) { Assert.Equal(data.Rows[index].Values, restored.DataSet.Rows[index].Values); Assert.Equal(data.Rows[index].Quality, restored.DataSet.Rows[index].Quality); Assert.True(Assert.Single(data.Rows[index].Sr830Ac).ContentEquals(Assert.Single(restored.DataSet.Rows[index].Sr830Ac))); }
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); var csv = await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")); Assert.Contains("ui_ac.sine_amplitude_v [Volt]", csv); Assert.Contains("ui_ac.reference_frequency_hz [Hertz]", csv); Assert.Contains("sr830_ac_json", csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

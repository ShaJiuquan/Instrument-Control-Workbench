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
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class PpmsImportOperatorTests
{
    private static string Fixture(string folder, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples", folder, name))) directory = directory.Parent;
        return Path.Combine((directory ?? throw new IOException("Example not found.")).FullName, "examples", folder, name);
    }
    [Fact]
    public async Task ActualPpmsFileImportsWithExplicitProfilesSavesExecutesSixRowsAndCanRestorePriorPlan()
    {
        var bench = new Bench(); await using var owner = bench.Shell; var vm = owner.Plans!;
        var before = vm.DocumentText; var beforeDirty = vm.IsDirty; var nativePath = Path.Combine(Path.GetTempPath(), "operator-import-" + Guid.NewGuid().ToString("N") + ".json");
        vm.DocumentPath = nativePath; vm.PpmsPath = Fixture("plans", "ppms-field-voltage-import.json"); var sourceText = File.ReadAllText(vm.PpmsPath);
        vm.UseRoutingProfiles = true; SetProfilePaths(vm); await vm.LoadImportProfilesCommand.ExecuteAsync(null);
        Assert.False(vm.HasError, vm.ProfileSummary); Assert.All(vm.ImportWirings, w => { Assert.False(w.IsIncluded); Assert.Null(w.Source); });
        Bind(vm); await vm.ImportPpmsCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message); Assert.NotEqual(before, vm.DocumentText);
        Assert.Equal(nativePath, vm.DocumentPath); Assert.True(vm.IsDirty); Assert.True(vm.IsValidated); Assert.Equal(3, vm.ValidatedPlan!.Metadata["ppms.root.0.0.0.source_line"].Value);
        foreach (var descriptor in bench.Runtime.Registry.Devices) Assert.False((await bench.Runtime.Registry.GetStatusAsync(descriptor.Id, CancellationToken.None)).Connected);
        try
        {
            await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError, vm.Message); var loaded = await bench.Workspace.LoadAsync(nativePath, CancellationToken.None);
            await owner.StartPlanCommand.ExecuteAsync(null); Assert.Equal(6, bench.Runs.LatestDataSet!.Rows.Count); Assert.NotNull(loaded.Plan.Routing);
            Assert.True((await bench.Runtime.PrimarySource.GetDeviceStatusAsync(CancellationToken.None)).SafetyConfirmed); Assert.False(bench.Runs.HasActiveRun);
            Assert.Equal(sourceText, File.ReadAllText(vm.PpmsPath)); vm.RestoreImportCommand.Execute(null);
            Assert.Equal(before, vm.DocumentText); Assert.Equal(beforeDirty, vm.IsDirty); Assert.False(vm.RestoreImportCommand.CanExecute(null));
        }
        finally { if (File.Exists(nativePath)) File.Delete(nativePath); }
    }
    [Theory]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("unmapped")]
    [InlineData("budget")]
    public async Task FailedImportPreservesTheValidatedPlanAndNeverConnects(string damage)
    {
        var bench = new Bench(budget: damage == "budget" ? 2 : ExperimentPlan.MaximumSupportedStepExecutions); await using var owner = bench.Shell; var vm = owner.Plans!;
        var path = Path.Combine(Path.GetTempPath(), "damaged-ppms-" + Guid.NewGuid().ToString("N") + ".json"); var before = vm.DocumentText;
        var text = damage switch
        {
            "malformed" => "bad JSON",
            "unsupported" => "{\"type\":\"measurement\",\"name\":\"unsupported-device-action\"}",
            "unmapped" => "{\"type\":\"measurement\",\"name\":\"k2450.set_output\",\"params\":{\"instrument\":\"missing\",\"enabled\":true}}",
            _ => "{\"type\":\"repeat\",\"params\":{\"num_cycles\":10},\"children\":[{\"type\":\"delay\",\"params\":{\"delay_s\":0}}]}",
        };
        try
        {
            File.WriteAllText(path, text); vm.PpmsPath = path; await vm.ImportPpmsCommand.ExecuteAsync(null);
            Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText); Assert.True(vm.IsValidated); Assert.False(vm.RestoreImportCommand.CanExecute(null)); Assert.Equal(text, File.ReadAllText(path));
            if (damage == "budget") Assert.Contains("execution budget", vm.Message);
            foreach (var d in bench.Runtime.Registry.Devices) Assert.False((await bench.Runtime.Registry.GetStatusAsync(d.Id, CancellationToken.None)).Connected);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task InvalidRoleAndChangedProfilePathsCannotReuseAStaleRoutingCatalog()
    {
        var bench = new Bench(); await using var owner = bench.Shell; var vm = owner.Plans!; var before = vm.DocumentText;
        vm.UseRoutingProfiles = true; vm.PpmsPath = Fixture("plans", "ppms-field-voltage-import.json"); SetProfilePaths(vm); await vm.LoadImportProfilesCommand.ExecuteAsync(null); Bind(vm);
        var row = vm.ChannelWiring!; row.Source = new(bench.Runtime.Registry.Devices.Single(d => d.Id.Value == "demo-field"));
        await vm.ImportPpmsCommand.ExecuteAsync(null); Assert.True(vm.HasError); Assert.Equal(before, vm.DocumentText);
        vm.ChannelsPath += ".changed"; Assert.Empty(vm.ImportWirings); Assert.Null(vm.ChannelWiring); Assert.False(vm.ImportPpmsCommand.CanExecute(null));
        row.Source = row.Sources[0]; Assert.Empty(vm.ImportWirings); // obsolete row callbacks cannot revive the catalog
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndCloseDrainActualNoncooperativeImportAndNeverApplyItsLateResult(bool close)
    {
        var importer = new HeldImporter(); var bench = new Bench(importer); await using var owner = bench.Shell; var vm = owner.Plans!; vm.PpmsPath = "chosen.json"; var before = vm.DocumentText;
        var import = vm.ImportPpmsCommand.ExecuteAsync(null); await importer.Started.Task; Assert.True(vm.IsDocumentBusy); Assert.False(owner.StartPlanCommand.CanExecute(null)); Assert.False(vm.LoadCommand.CanExecute(null));
        Task? disposal = null;
        if (close) { disposal = vm.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted); }
        else { vm.CancelPpmsCommand.Execute(null); Assert.True(vm.IsDocumentBusy); Assert.False(vm.CancelPpmsCommand.CanExecute(null)); }
        importer.Release.SetResult(new("late", new DelayStep(new("late"), TimeSpan.Zero))); await import;
        if (disposal is not null) await disposal;
        Assert.Equal(before, vm.DocumentText); Assert.False(vm.IsDocumentBusy); Assert.Equal(1, importer.Calls); Assert.False(vm.RestoreImportCommand.CanExecute(null));
    }
    [Fact]
    public async Task RequestIsCapturedBeforeBusyCallbacksAndChangedInputsDiscardRatherThanRedirectItsResult()
    {
        var importer = new HeldImporter(); var bench = new Bench(importer); await using var owner = bench.Shell;
        PlanWorkspaceViewModel? vm = null;
        await using var editor = vm = new(bench.Workspace, bench.Initial, "native.json", () => true, () => { if (vm?.IsDocumentBusy == true) vm.PpmsPath = "redirect.json"; });
        vm.PpmsPath = "chosen.json"; var before = vm.DocumentText; var job = vm.ImportPpmsCommand.ExecuteAsync(null); await importer.Started.Task;
        Assert.Equal("chosen.json", importer.Path); importer.Release.SetResult(new("late", new DelayStep(new("late"), TimeSpan.Zero))); await job;
        Assert.Equal(before, vm.DocumentText); Assert.False(vm.RestoreImportCommand.CanExecute(null)); Assert.Contains("变化", vm.Message);
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("paths")]
    [InlineData("close")]
    public async Task ProfileReadCancellationOrPathChangesNeverPublishAStaleCatalog(string action)
    {
        var loader = new HeldProfiles(); var bench = new Bench(profiles: loader); await using var owner = bench.Shell; var vm = owner.Plans!; SetProfilePaths(vm);
        var originalPath = vm.ChannelsPath; var job = vm.LoadImportProfilesCommand.ExecuteAsync(null); await loader.Started.Task;
        Task? disposal = null;
        if (action == "cancel") vm.CancelPpmsCommand.Execute(null);
        else if (action == "close") { disposal = vm.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted); }
        else vm.ChannelsPath = "changed.json";
        Assert.True(vm.IsDocumentBusy); Assert.Equal(originalPath, loader.Path);
        loader.Release.SetResult(new PpmsRoutingProfileImporter().Parse(File.ReadAllText(Fixture("profiles", "channel_profiles.json")), File.ReadAllText(Fixture("profiles", "samples.json")), File.ReadAllText(Fixture("profiles", "instrument_ports.json"))));
        await job; if (disposal is not null) await disposal;
        Assert.Empty(vm.ImportWirings); Assert.False(vm.IsDocumentBusy); Assert.False(vm.HasError);
    }
    [Fact]
    public async Task DirtyNodeAndRunAdmissionBlockDirectImportAndProfileReadCommands()
    {
        var importer = new HeldImporter(); var bench = new Bench(importer); await using var owner = bench.Shell; var vm = owner.Plans!;
        vm.PpmsPath = "chosen.json"; SetProfilePaths(vm); vm.GraphNodes.Single(n => n.Id.Value == "before").SelectCommand.Execute(null); vm.NodeForm!.Seconds = "2";
        await vm.ImportPpmsCommand.ExecuteAsync(null); await vm.LoadImportProfilesCommand.ExecuteAsync(null); Assert.Equal(0, importer.Calls); Assert.Empty(vm.ImportWirings);
        vm.DiscardNodeCommand.Execute(null); owner.IsBusy = true; vm.RefreshCommands(); await vm.ImportPpmsCommand.ExecuteAsync(null); Assert.Equal(0, importer.Calls); owner.IsBusy = false;
    }
    [Fact]
    public async Task FailedProfileReloadKeepsThePlanButDoesNotSilentlyImportUsingAnOlderCatalog()
    {
        var loader = new FailedReloadProfiles(); var bench = new Bench(profiles: loader); await using var owner = bench.Shell; var vm = owner.Plans!;
        vm.UseRoutingProfiles = true; vm.PpmsPath = "chosen.json"; SetProfilePaths(vm); await vm.LoadImportProfilesCommand.ExecuteAsync(null); Bind(vm);
        Assert.True(vm.ImportPpmsCommand.CanExecute(null)); var before = vm.DocumentText;
        await vm.LoadImportProfilesCommand.ExecuteAsync(null); Assert.True(vm.HasError); Assert.NotEmpty(vm.ImportWirings);
        Assert.False(vm.ImportPpmsCommand.CanExecute(null)); await vm.ImportPpmsCommand.ExecuteAsync(null); Assert.Equal(before, vm.DocumentText); Assert.Contains("重新载入", vm.ProfileSummary);
    }
    private static void SetProfilePaths(PlanWorkspaceViewModel vm)
    { vm.ChannelsPath = Fixture("profiles", "channel_profiles.json"); vm.SamplesPath = Fixture("profiles", "samples.json"); vm.InstrumentPortsPath = Fixture("profiles", "instrument_ports.json"); }
    private static void Bind(PlanWorkspaceViewModel vm)
    {
        var row = vm.ImportWirings.Single(w => w.Layout.Id.Value == "k2450"); row.IsIncluded = true; row.Source = row.Sources.Single(d => d.Descriptor.Id.Value == "demo-smu");
        vm.ChannelWiring = row; vm.ImportMatrix = vm.ImportMatrices.Single();
    }
    private sealed class HeldImporter : IExperimentPlanImporter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ExperimentPlan> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public ExperimentPlan Parse(string text, ExperimentImportBindings bindings) => throw new NotSupportedException();
        public Task<ExperimentPlan> LoadAsync(string path, ExperimentImportBindings bindings, CancellationToken token)
        { Calls++; Path = path; Started.TrySetResult(); return Release.Task; }
    }
    private sealed class HeldProfiles : IRoutingProfileImporter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RoutingProfileCatalog> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Path { get; private set; }
        public RoutingProfileCatalog Parse(string c, string s, string p) => throw new NotSupportedException();
        public Task<RoutingProfileCatalog> LoadAsync(string c, string s, string p, CancellationToken token)
        { Path = c; Started.TrySetResult(); return Release.Task; }
    }
    private sealed class FailedReloadProfiles : IRoutingProfileImporter
    {
        private int reads;
        public RoutingProfileCatalog Parse(string c, string s, string p) => new PpmsRoutingProfileImporter().Parse(c, s, p);
        public Task<RoutingProfileCatalog> LoadAsync(string c, string s, string p, CancellationToken token) => ++reads == 1
            ? Task.FromResult(Parse(File.ReadAllText(c), File.ReadAllText(s), File.ReadAllText(p)))
            : Task.FromException<RoutingProfileCatalog>(new FormatException("Changed profile is malformed."));
    }
    private sealed class Bench
    {
        public ConfiguredDeviceRuntime Runtime { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MainWindowViewModel Shell { get; }
        public ExperimentPlan Initial { get; }
        public Bench(IExperimentPlanImporter? importer = null, IRoutingProfileImporter? profiles = null, long budget = ExperimentPlan.MaximumSupportedStepExecutions)
        {
            var initial = DemoDeviceConfiguration.Create(); var config = new DeviceConfigurationCatalog(initial.PrimarySource,
                initial.Devices.Concat([new DeviceConfiguration(new("matrix"), "K3706", "Simulation matrix", ConfiguredDeviceCategory.Matrix, new SimulationDeviceConnection(), matrix: new([new(1, 8, 10)], [new("demo-smu")]))]),
                initial.Aliases.Concat([KeyValuePair.Create("k3706", new InstrumentId("matrix"))]));
            var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); Runtime = new(config, new ConfiguredDeviceFactory(), leases, hub);
            Runs = new(Runtime.PrimarySource, leases, hub, deviceRegistry: Runtime.Registry);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, importer ?? new PpmsExperimentPlanImporter(), profiles ?? new PpmsRoutingProfileImporter(), Runtime);
            Initial = new("before import", new SequenceStep(new("root"), [new DelayStep(new("before"), TimeSpan.Zero)]),
                [new(new("demo-smu"), SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.000105, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))], maximumStepExecutions: budget);
            Shell = new(Runtime.PrimarySource, Runs, hub, Workspace, Initial, "native.json", registry: Runtime.Registry);
        }
    }
}

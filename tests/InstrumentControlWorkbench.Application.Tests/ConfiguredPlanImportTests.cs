using System.Text.Json.Nodes;
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

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class ConfiguredPlanImportTests
{
    private static string Fixture(string folder, string name) => Path.Combine(AppContext.BaseDirectory, folder, name);
    private static RoutingProfileCatalog Profiles() => new PpmsRoutingProfileImporter().Parse(File.ReadAllText(Fixture("profiles", "channel_profiles.json")), File.ReadAllText(Fixture("profiles", "samples.json")), File.ReadAllText(Fixture("profiles", "instrument_ports.json")));
    [Fact]
    public async Task ConfiguredAliasesPreparationsAndProfileBindingsExecuteTheActualFixtureInTheSharedRegistry()
    {
        var bench = new Bench(); var profile = Profiles(); var rows = new List<ImportWiringSelection> { new(new("k2450"), new("smu")) };
        var selection = new ImportRoutingSelection(profile, new("matrix"), rows, new("k2450"), TimeSpan.Zero);
        rows[0] = new(new("k2450"), new("field")); // caller list is not the captured routing request
        var bindings = bench.Workspace.CreateConfiguredImportBindings(bench.Initial, selection);
        Assert.Equal(new InstrumentId("smu"), bindings.Instruments["k2450"]); Assert.Same(bench.Initial.Preparations[0], Assert.Single(bindings.Preparations));
        Assert.Equal(new InstrumentId("smu"), bindings.ChannelProfiles!.Wiring.SourceMeter);
        var doc = await bench.Workspace.ImportAsync(Fixture("plans", "ppms-field-voltage-import.json"), bindings, CancellationToken.None, 500);
        Assert.Equal(500, doc.Plan.MaximumStepExecutions); Assert.NotNull(doc.Plan.Routing);
        foreach (var d in bench.Runtime.Registry.Devices) Assert.False((await bench.Runtime.Registry.GetStatusAsync(d.Id, CancellationToken.None)).Connected);
        await bench.Runs.StartPlanAsync(doc.Plan, CancellationToken.None); Assert.Equal(6, bench.Runs.LatestDataSet!.Rows.Count);
        Assert.True((await bench.Runtime.PrimarySource.GetDeviceStatusAsync(CancellationToken.None)).SafetyConfirmed); Assert.False(bench.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("matrix")]
    [InlineData("source")]
    [InlineData("meter")]
    [InlineData("unknown")]
    public async Task WrongRegisteredRolesAndUnknownBindingsAreRejectedWithoutConnection(string damage)
    {
        var b = new Bench(); var selection = new ImportRoutingSelection(Profiles(), new(damage == "matrix" ? "field" : "matrix"),
            [new(new("k2450"), new(damage == "source" ? "field" : damage == "unknown" ? "absent" : "smu"), damage == "meter" ? new InstrumentId("smu") : null)], new("k2450"), TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => b.Workspace.CreateConfiguredImportBindings(b.Initial, selection));
        foreach (var d in b.Runtime.Registry.Devices) Assert.False((await b.Runtime.Registry.GetStatusAsync(d.Id, CancellationToken.None)).Connected);
    }
    [Fact]
    public async Task ConfiguredImportPreservesPureCurrentSourceAndStandaloneMeterRoles()
    {
        var b = new Bench(independent: true); var bindings = b.Workspace.CreateConfiguredImportBindings(b.Initial);
        Assert.Same(Assert.Single(b.Initial.CurrentSourcePreparations), Assert.Single(bindings.CurrentSourcePreparations)); Assert.Contains(new InstrumentId("meter"), bindings.StandaloneVoltageMeters);
        var document = await b.Workspace.ImportAsync(Fixture("plans", "ppms-current-resistance-import.json"), bindings, CancellationToken.None, 500);
        Assert.Single(document.Plan.CurrentSourcePreparations); Assert.Equal(new InstrumentId("current"), document.Plan.CurrentSourcePreparations[0].Device);
        foreach (var d in b.Runtime.Registry.Devices) Assert.False((await b.Runtime.Registry.GetStatusAsync(d.Id, CancellationToken.None)).Connected);
        await b.Runs.StartPlanAsync(document.Plan, CancellationToken.None); Assert.Equal(2, b.Runs.LatestDataSet!.Rows.Count);
        Assert.True((await b.Runtime.Registry.GetStatusAsync(new("current"), CancellationToken.None)).SafetyConfirmed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public void MetadataEquivalentButDifferentRegistryCannotSupplyImportRuntimeAuthority()
    {
        var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var config = new DeviceConfigurationCatalog(new("smu"), [new(new("smu"), "K2450", "Simulation source", ConfiguredDeviceCategory.SourceMeter, new SimulationDeviceConnection())], [KeyValuePair.Create("k2450", new InstrumentId("smu"))]);
        var runtime = new ConfiguredDeviceRuntime(config, new ConfiguredDeviceFactory(), leases, hub);
        var clone = new DeviceRegistry([runtime.PrimarySource], new(TimeSpan.FromSeconds(5))); Assert.True(runtime.Registry.Devices.SequenceEqual(clone.Devices));
        var runs = new MeasurementRunService(clone, hub); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter(), runtime: runtime);
        Assert.Throws<InvalidOperationException>(() => workspace.CreateConfiguredImportBindings(new("plan", new DelayStep(new("delay"), TimeSpan.Zero))));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000001)]
    public async Task InvalidBudgetIsRejectedBeforeTheImporterReadsAnything(long budget)
    {
        var fake = new CountingImporter(); var b = new Bench(fake); var bindings = b.Workspace.CreateConfiguredImportBindings(b.Initial);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => b.Workspace.ImportAsync("unused", bindings, CancellationToken.None, budget)); Assert.Equal(0, fake.Reads);
    }
    [Fact]
    public async Task AlreadyCancelledImportNeverStartsAnUncooperativeReader()
    {
        var fake = new CountingImporter(); var b = new Bench(fake); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Workspace.ImportAsync("unused", b.Workspace.CreateConfiguredImportBindings(b.Initial), cancellation.Token)); Assert.Equal(0, fake.Reads);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]
    public async Task EmptyMeasurementLabelsUseTheNodeNameWhileRouteSafetyRemainsAuthoritative(string channel)
    {
        var b = new Bench(); var bindings = b.Workspace.CreateConfiguredImportBindings(b.Initial, new(Profiles(), new("matrix"), [new(new("k2450"), new("smu"))], new("k2450"), TimeSpan.Zero));
        var root = JsonNode.Parse(File.ReadAllText(Fixture("plans", "ppms-field-voltage-import.json")))!;
        var scan = root["children"]![0]!["children"]![0]!; var child = scan["children"]![0]!;
        child["params"]!["channel"] = channel;
        if (channel == "none") { var error = Assert.Throws<FormatException>(() => b.Workspace.Import(root.ToJsonString(), bindings)); Assert.Contains("cannot change or bypass", error.Message); return; }
        // Two reads force group label creation rather than relying only on a single-child flush.
        ((JsonArray)scan["children"]!).Add(new JsonObject { ["type"] = "measurement", ["name"] = "read_field", ["params"] = new JsonObject() });
        var document = b.Workspace.Import(root.ToJsonString(), bindings); await b.Runs.StartPlanAsync(document.Plan, CancellationToken.None);
        Assert.Equal(18, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(6, b.Runs.LatestDataSet.Rows.Count(r => r.MeasurementGroup is not null));
        Assert.All(b.Runs.LatestDataSet.Rows.Where(r => r.MeasurementGroup is not null), r => Assert.Contains("meas_current", r.MeasurementGroup!.Inputs.Select(i => i.Label)));
    }
    private sealed class CountingImporter : IExperimentPlanImporter
    {
        public int Reads { get; private set; }
        public ExperimentPlan Parse(string text, ExperimentImportBindings bindings) => throw new NotSupportedException();
        public Task<ExperimentPlan> LoadAsync(string path, ExperimentImportBindings bindings, CancellationToken token) { Reads++; throw new NotSupportedException(); }
    }
    private sealed class Bench
    {
        public ConfiguredDeviceRuntime Runtime { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public ExperimentPlan Initial { get; }
        public Bench(IExperimentPlanImporter? importer = null, bool independent = false)
        {
            var devices = new List<DeviceConfiguration> {
                new(new("smu"), "K2450", "Simulation source", ConfiguredDeviceCategory.SourceMeter, new SimulationDeviceConnection()),
                new(new("field"), "SIM-FIELD", "Simulation field", ConfiguredDeviceCategory.Field, new SimulationDeviceConnection()),
                new(new("temperature"), "SIM-TEMPERATURE", "Simulation temperature", ConfiguredDeviceCategory.Temperature, new SimulationDeviceConnection()),
                new(new("angle"), "ROTATOR", "Simulation angle", ConfiguredDeviceCategory.Rotation, new SimulationDeviceConnection()),
                new(new("matrix"), "K3706", "Simulation matrix", ConfiguredDeviceCategory.Matrix, new SimulationDeviceConnection(), matrix: new([new(1, 8, 10)], [new("smu")])) };
            var aliases = new Dictionary<string, InstrumentId> { ["k2450"] = new("smu"), ["field"] = new("field"), ["temperature"] = new("temperature"), ["angle"] = new("angle"), ["k3706"] = new("matrix") };
            if (independent)
            {
                devices.Add(new(new("current"), "K6221", "Simulation current source", ConfiguredDeviceCategory.Source, new SimulationDeviceConnection()));
                devices.Add(new(new("meter"), "K2182", "Simulation voltmeter", ConfiguredDeviceCategory.Meter, new SimulationDeviceConnection()));
                aliases.Add("k6221", new("current")); aliases.Add("k2182", new("meter"));
            }
            var config = new DeviceConfigurationCatalog(new("smu"), devices, aliases);
            var hub = new NotificationHub(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5)); Runtime = new(config, new ConfiguredDeviceFactory(), leases, hub); Runs = new(Runtime.PrimarySource, leases, hub, deviceRegistry: Runtime.Registry);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, importer ?? new PpmsExperimentPlanImporter(), new PpmsRoutingProfileImporter(), Runtime);
            Initial = new("initial", new DelayStep(new("initial"), TimeSpan.Zero), [new(new("smu"), SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(.000105, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))],
                currentSourcePreparations: independent ? [new(new("current"), new(new(.00001, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))] : null);
        }
    }
}

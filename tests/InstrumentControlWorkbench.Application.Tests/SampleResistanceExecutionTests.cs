using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class SampleResistanceExecutionTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Theory]
    [InlineData(SampleTopology.TwoWire, 1, SourceFunction.Voltage)]
    [InlineData(SampleTopology.FourWire, 1, SourceFunction.Voltage)]
    [InlineData(SampleTopology.FourWire, 1, SourceFunction.Current)]
    [InlineData(SampleTopology.HallBar, 2, SourceFunction.Voltage)]
    [InlineData(SampleTopology.VanDerPauw, 8, SourceFunction.Voltage)]
    public async Task RecipesAcquireEveryAssignedRouteWithVerifiedSourceAndRowProvenance(SampleTopology topology, int count, SourceFunction function)
    {
        var b = new Bench(); var config = Configuration(topology); var recipe = Recipe(config, function);
        b.Listener.Row = async row =>
        {
            var route = Assert.IsType<ResolvedSampleRoute>(row.Routing);
            Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(route.Closed.ToHashSet().SetEquals((await b.Matrix.GetSnapshotAsync(Token)).Closed));
            Assert.Equal(route.Topology, topology);
            Assert.Equal(row.Values[new("voltage")].Value / row.Values[new("current")].Value, row.Values[new("resistance")].Value);
        };
        await b.Runs.StartSampleResistanceAsync(recipe, Token);
        Assert.Equal(count, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(count, b.Listener.Rows);
        Assert.Equal(count, b.Runs.LatestDataSet.Rows.Select(r => r.Routing!.Binding.Selection).Distinct().Count());
        Assert.All(b.Runs.LatestDataSet.Rows, r => config.ValidateContext(r.Routing!));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task NestedBiasScanPreservesTheLiveSetpointAcrossAllVdpPermutations()
    {
        var b = new Bench(); var config = Configuration(SampleTopology.VanDerPauw); var basePlan = SampleResistancePlan.Create(Recipe(config));
        var scan = new ScanStep(new("bias_scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("bias"),
            new([new(0.01, PhysicalUnit.Volt), new(0.02, PhysicalUnit.Volt)]), TimeSpan.Zero, basePlan.Root);
        await b.Runs.StartPlanAsync(new("bias x vdP", scan, basePlan.Preparations, routing: config), Token);
        Assert.Equal(16, b.Runs.LatestDataSet!.Rows.Count);
        Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Equal(row.Values[new("bias")], row.Values[new("voltage")]));
        Assert.Contains(new ParameterId("bias"), b.Runs.LatestDataSet.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("resistance")).DependsOn);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task PlanAndArchiveRoundTripAllProfilesAndPerRowRoutingThenCsvKeepsQuotedContext()
    {
        using var folder = new TemporaryFolder(); RunId id;
        var config = Configuration(SampleTopology.HallBar);
        await using (var archive = new FileRunArchive(Path.Combine(folder.Path, "runs")))
        {
            var b = new Bench(archive); var plan = SampleResistancePlan.Create(Recipe(config)); var codec = new JsonExperimentPlanDocument();
            var path = Path.Combine(folder.Path, "sample.json"); await codec.SaveAsync(path, plan, Token); plan = await codec.LoadAsync(path, Token);
            Assert.NotNull(plan.Routing); Assert.Equal(config.Samples[0].Name, plan.Routing.Samples[0].Name);
            await b.Runs.StartPlanAsync(plan, Token); id = b.Runs.LatestDataSet!.Id;
        }
        await using var reopened = new FileRunArchive(Path.Combine(folder.Path, "runs"));
        var loaded = await reopened.LoadAsync(id, Token);
        Assert.Equal(2, loaded.DataSet.Rows.Count);
        Assert.Equal(new[] { SampleRouteSelection.HallLongitudinal, SampleRouteSelection.HallTransverse }, loaded.DataSet.Rows.Select(r => r.Routing!.Binding.Selection));
        Assert.All(loaded.DataSet.Rows, r => loaded.DataSet.Plan!.Routing!.ValidateContext(r.Routing!));
        var csv = Path.Combine(folder.Path, "sample.csv"); await reopened.ExportCsvAsync(id, csv, Token);
        var text = await File.ReadAllTextAsync(csv); Assert.Contains("routing_json", text);
        Assert.Contains("HallLongitudinal", text); Assert.Contains("HallTransverse", text); Assert.Contains("sample", text);
        Assert.Contains("\"\"sourceMeter\"\"", text); Assert.Contains("试样", text);
    }

    [Fact]
    public async Task TwoSamplesOnDifferentChannelProfilesRetainTheirOwnRowIdentity()
    {
        var b = new Bench(); var config = Configuration(SampleTopology.FourWire);
        var second = new ChannelProfile(new("channel2"), "second", config.Channels[0].Connections.Select((c, i) => new ChannelConnection(c.Key, 1, i + 3)));
        var sample = new SampleProfile(new("second"), "second sample", SampleTopology.FourWire,
            config.Samples[0].Electrodes.Select(e => new SampleElectrode(e.Label, second.Id, e.Connection)));
        config = new(config.Matrix, [.. config.Channels, second], config.Wirings, [.. config.Samples, sample]);
        var firstRead = Read("first", new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance));
        var secondRead = Read("second", new(new("second"), new("wiring"), SampleRouteSelection.FourWireResistance));
        await b.Runs.StartPlanAsync(new("two samples", new SequenceStep(new("root"), [firstRead, secondRead]), [Preparation(SensingMode.FourWire)], routing: config), Token);
        Assert.Equal(new[] { "sample", "second" }, b.Runs.LatestDataSet!.Rows.Select(r => r.Routing!.Binding.Sample.Value));
        Assert.Equal(new[] { "channel", "channel2" }, b.Runs.LatestDataSet.Rows.Select(r => r.Routing!.Roles[0].Channel.Value));
    }

    [Theory]
    [InlineData("missing_config")]
    [InlineData("wrong_mode")]
    [InlineData("wrong_source")]
    [InlineData("out_of_card")]
    [InlineData("not_guarded")]
    public async Task InvalidBindingsFailWholePlanPreflightBeforeAnyDriverConnect(string failure)
    {
        var b = new Bench(guardedSource: failure == "not_guarded" ? new("different_source") : null);
        var config = Configuration(SampleTopology.FourWire);
        if (failure == "wrong_source") config = new(config.Matrix, config.Channels,
            [new(new("wiring"), new("different_source"), new(1, 1), new(1, 2), new(1, 3), new(1, 4))], config.Samples);
        if (failure == "out_of_card") config = new(config.Matrix,
            [new(new("channel"), "bad", config.Channels[0].Connections.Select((c, i) => new ChannelConnection(c.Key, 1, i == 0 ? 7 : c.Column)))], config.Wirings, config.Samples);
        var read = Read("read", new(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance));
        var plan = new ExperimentPlan("invalid binding", read, [Preparation(failure == "wrong_mode" ? SensingMode.TwoWire : SensingMode.FourWire)],
            routing: failure == "missing_config" ? null : config);
        Assert.NotNull(await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)));
        Assert.Equal(0, b.Matrix.Connects); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.Empty(b.Runs.History.Runs); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ExternallyManagedRouteReadsWithoutSwitchingOrDisablingTheCallersOutput()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var config = Configuration(SampleTopology.FourWire);
        var binding = new SampleRoutingBinding(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance);
        var plan = new ExperimentPlan("externally managed", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), config.Matrix, config.Resolve(binding).Closed, TimeSpan.Zero),
            new SourceOutputStep(new("on"), b.Source.Id, true), Read("read", binding, externallyManaged: true), new DelayStep(new("wait"), TimeSpan.FromSeconds(30))]),
            [Preparation(SensingMode.FourWire)], routing: config);
        var task = b.Runs.StartPlanAsync(plan, Token); await Until(events, WorkbenchNotificationKind.StepStarted, "wait");
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Matrix.Writes);
        Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }

    [Fact]
    public async Task ExternallyManagedRouteMismatchCannotProduceAResistanceRow()
    {
        var b = new Bench(); var config = Configuration(SampleTopology.FourWire);
        var binding = new SampleRoutingBinding(new("sample"), new("wiring"), SampleRouteSelection.FourWireResistance);
        var plan = new ExperimentPlan("wrong route", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), config.Matrix, [new(1, 1, 1)], TimeSpan.Zero),
            new SourceOutputStep(new("on"), b.Source.Id, true), Read("read", binding, externallyManaged: true)]), [Preparation(SensingMode.FourWire)], routing: config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(plan, Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ActualSensingModeChangedAfterPreparationFailsBeforeAcquisition()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var config = Configuration(SampleTopology.FourWire);
        b.Listener.Started = async () =>
        {
            // Model a front-panel mode change after the frozen preparation.
            var snapshot = await b.Source.GetSnapshotAsync(Token);
            await b.Source.ConfigureSenseAsync(snapshot.Sense with { Mode = SensingMode.TwoWire }, Token);
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartSampleResistanceAsync(Recipe(config), Token));
        Assert.Contains("Actual sensing mode", error.Message); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        while (events.Reader.TryRead(out var fact)) Assert.False(fact.Snapshot?.OutputEnabled == true);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task PauseDuringRouteSettlingKeepsConnectionsThenAbortCleansWithoutAcquiring()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var recipe = Recipe(Configuration(SampleTopology.FourWire), settle: TimeSpan.FromSeconds(30));
        var task = b.Runs.StartSampleResistanceAsync(recipe, Token); await Until(events, WorkbenchNotificationKind.MatrixRoutingChanged);
        b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Equal(4, (await b.Matrix.GetSnapshotAsync(Token)).Closed.Count); Assert.Equal(1, b.Matrix.Writes);
        b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task CancelledListenerKeepsCommittedRoutingContextAndArchiveFinalizationReconcilesIt()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        var b = new Bench(archive); using var cancellation = new CancellationTokenSource();
        b.Listener.Row = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await b.Runs.StartSampleResistanceAsync(Recipe(Configuration(SampleTopology.FourWire)), cancellation.Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.NotNull(row.Routing);
        var loaded = await archive.LoadAsync(b.Runs.LatestDataSet.Id, Token);
        Assert.Equal(row.Routing!.Roles, Assert.Single(loaded.DataSet.Rows).Routing!.Roles);
        Assert.Equal(MeasurementRunState.Aborted, loaded.DataSet.State); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task TamperedUnfinalizedRowContextCannotBeLoadedOrReplaceAnExistingCsv()
    {
        using var folder = new TemporaryFolder(); var b = new Bench(); var plan = SampleResistancePlan.Create(Recipe(Configuration(SampleTopology.FourWire)));
        await b.Runs.StartPlanAsync(plan, Token); var snapshot = b.Runs.LatestDataSet!.Capture();
        var runPath = Path.Combine(folder.Path, "runs");
        await using (var archive = new FileRunArchive(runPath))
        {
            var running = snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = Array.Empty<MeasurementDataRow>() };
            await archive.OnStartedAsync(running, Token); await archive.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token);
        }
        var rowPath = Path.Combine(runPath, snapshot.Id.Value.ToString("N"), "rows.ndjson");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(rowPath))!;
        json["routing"]!["roles"]![0]!["column"] = 5; await File.WriteAllTextAsync(rowPath, json.ToJsonString() + "\n");
        await using var reopened = new FileRunArchive(runPath);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.LoadAsync(snapshot.Id, Token));
        var export = Path.Combine(folder.Path, "existing.csv"); await File.WriteAllTextAsync(export, "original");
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ExportCsvAsync(snapshot.Id, export, Token));
        Assert.Equal("original", await File.ReadAllTextAsync(export));
    }

    [Fact]
    public void PlanCodecRejectsUnknownRoutingFieldsAndWrongEnumWithoutChangingLegacyPlans()
    {
        var codec = new JsonExperimentPlanDocument(); var plan = SampleResistancePlan.Create(Recipe(Configuration(SampleTopology.FourWire)));
        var json = JsonNode.Parse(codec.Serialize(plan))!; json["routing"]!["unexpected"] = true;
        Assert.Throws<FormatException>(() => codec.Parse(json.ToJsonString()));
        json = JsonNode.Parse(codec.Serialize(plan))!; json["routing"]!["samples"]![0]!["topology"] = "unknown";
        Assert.Throws<FormatException>(() => codec.Parse(json.ToJsonString()));
        var legacy = new ExperimentPlan("legacy", new DelayStep(new("wait"), TimeSpan.Zero));
        Assert.Null(codec.Parse(codec.Serialize(legacy)).Routing); Assert.DoesNotContain("routing", codec.Serialize(legacy));
    }

    [Fact]
    public async Task RepositoryFourWireSampleFileRunsThreeBiasPointsFromDisk()
    {
        var b = new Bench();
        var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "sample-four-wire.plan.json"), Token);
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count);
        Assert.All(b.Runs.LatestDataSet.Rows, row =>
        {
            Assert.Equal("four_wire_sample", row.Routing!.Binding.Sample.Value);
            Assert.Equal(4, row.Routing.Closed.Count);
            Assert.Equal(row.Values[new("bias")], row.Values[new("voltage")]);
            plan.Routing!.ValidateContext(row.Routing);
        });
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task ChangedDeliveredRoutingContextCannotFinalizeOrBeAcceptedAsANewRow()
    {
        using var folder = new TemporaryFolder(); var b = new Bench();
        await b.Runs.StartSampleResistanceAsync(Recipe(Configuration(SampleTopology.FourWire)), Token);
        var snapshot = b.Runs.LatestDataSet!.Capture(); var original = snapshot.Rows[0]; var route = original.Routing!;
        var changedContext = new ResolvedSampleRoute(route.Binding, "renamed", route.Topology, route.Matrix, route.SourceMeter, route.Roles);
        var changed = new MeasurementDataRow(original.Timestamp, original.Values, changedContext);
        await using var archive = new FileRunArchive(folder.Path);
        await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = Array.Empty<MeasurementDataRow>() }, Token);
        await Assert.ThrowsAsync<ArgumentException>(() => archive.OnRowAsync(snapshot.Id, changed, Token));
        await archive.OnRowAsync(snapshot.Id, original, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => archive.OnFinalizedAsync(snapshot with { Rows = new[] { changed } }, Token));
    }

    [Fact]
    public async Task BrokenSampleInUnselectedBranchIsRejectedBeforeAdmission()
    {
        var b = new Bench(); var config = Configuration(SampleTopology.FourWire); var sample = config.Samples[0];
        var bad = new SampleProfile(new("bad"), "disabled sample", sample.Topology,
            sample.Electrodes.Select((e, i) => new SampleElectrode(e.Label, e.Channel, e.Connection, i != 2)));
        config = new(config.Matrix, config.Channels, config.Wirings, [sample, bad]);
        var root = new SequenceStep(new("root"), [new AssignValueStep(new("assign"), new("flag"), new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
            new IfElseStep(new("choose"), new(new("flag"), ComparisonOperator.Equal, new ConstantValue(new(1, PhysicalUnit.Dimensionless))),
                Read("good", new(sample.Id, new("wiring"), SampleRouteSelection.FourWireResistance)),
                Read("bad", new(bad.Id, new("wiring"), SampleRouteSelection.FourWireResistance)))]);
        await Assert.ThrowsAsync<ArgumentException>(() => b.Runs.StartPlanAsync(new("invalid branch", root, [Preparation(SensingMode.FourWire)], routing: config), Token));
        Assert.Equal(0, b.Matrix.Connects); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False(b.Runs.HasActiveRun);
    }

    [Fact]
    public async Task CancellingVdpAfterThirdAcquisitionKeepsTheThreeTypedPartialRows()
    {
        var b = new Bench(); using var cancellation = new CancellationTokenSource();
        b.Listener.Row = _ => { if (b.Listener.Rows == 3) cancellation.Cancel(); return Task.CompletedTask; };
        await b.Runs.StartSampleResistanceAsync(Recipe(Configuration(SampleTopology.VanDerPauw)), cancellation.Token);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State);
        Assert.Equal(new[] { SampleRouteSelection.VdpAbCd, SampleRouteSelection.VdpBaCd, SampleRouteSelection.VdpAbDc },
            b.Runs.LatestDataSet!.Rows.Select(r => r.Routing!.Binding.Selection));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }

    private static ReadSampleResistanceStep Read(string id, SampleRoutingBinding binding, bool externallyManaged = false)
        => new(new(new(id), new("k2450"), SourceFunction.Voltage, new("voltage"), new("current"), new("resistance"), new(1e-15, PhysicalUnit.Ampere)), binding, TimeSpan.Zero, externallyManaged);
    private static SourcePreparation Preparation(SensingMode mode, SourceFunction function = SourceFunction.Voltage)
        => new(new("k2450"), function, new(function == SourceFunction.Voltage ? 0.01 : 1e-6, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere),
            new(function == SourceFunction.Voltage ? 0.001 : 20, function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt),
            new(function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage, mode, 1, true));
    private static SampleResistanceRecipe Recipe(RoutingConfiguration config, SourceFunction function = SourceFunction.Voltage, TimeSpan? settle = null)
        => new("sample R", config, new("sample"), new("wiring"), Preparation(config.Samples[0].Topology == SampleTopology.TwoWire ? SensingMode.TwoWire : SensingMode.FourWire, function),
            settle ?? TimeSpan.Zero, new(1e-15, PhysicalUnit.Ampere));
    private static RoutingConfiguration Configuration(SampleTopology topology)
    {
        var labels = SampleProfile.RequiredLabels(topology); var shared = topology == SampleTopology.TwoWire;
        return new(new("matrix"), [new(new("channel"), "CH1", labels.Select((label, i) => new ChannelConnection(new($"p{i}"), 1, i + 1)))],
            [new(new("wiring"), new("k2450"), new(1, 1), new(1, 2), new(1, shared ? 1 : 3), new(1, shared ? 2 : 4))],
            [new(new("sample"), "试样, \"A\"\nB", topology, labels.Select((label, i) => new SampleElectrode(new(label), new("channel"), new($"p{i}"))))]);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind, string? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var item = await events.Reader.ReadAsync(deadline.Token); if (item.Kind == kind && (step is null || item.Step?.Value == step)) return; }
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public NotificationHub Hub { get; } = new();
        public RecordingListener Listener { get; } = new();
        public RecordingMatrix Matrix { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null, InstrumentId? guardedSource = null)
        {
            var definition = new MatrixSwitchDefinition(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []),
                [new(1, 4, 6)], [guardedSource ?? Source.Id]);
            Matrix = new(definition, Source); var manager = new InstrumentManager(Source, Hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            Runs = new(manager, leases, Hub, archive is null ? [Listener] : [Listener, archive], deviceRegistry: new([manager, new MatrixSwitchSession(Matrix, Hub)], leases));
        }
    }
    private sealed class RecordingMatrix(MatrixSwitchDefinition definition, MockKeithley2450SourceMeter source) : IMatrixSwitchPort
    {
        private readonly MockMatrixSwitch inner = new(definition);
        public MatrixSwitchDefinition Definition => definition;
        public int Connects { get; private set; }
        public int Writes { get; private set; }
        public Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) { Connects++; return inner.ConnectAsync(token); }
        public Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public async Task OpenAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        { Assert.False((await source.GetSnapshotAsync(Token)).OutputEnabled); Writes++; await inner.OpenAsync(points, token); }
        public async Task CloseAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        { Assert.False((await source.GetSnapshotAsync(Token)).OutputEnabled); Writes++; await inner.CloseAsync(points, token); }
    }
    private sealed class RecordingListener : IRunResultListener
    {
        public Func<MeasurementDataRow, Task>? Row { get; set; }
        public Func<Task>? Started { get; set; }
        public int Rows { get; private set; }
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Started?.Invoke() ?? Task.CompletedTask;
        public async Task OnRowAsync(RunId id, MeasurementDataRow row, CancellationToken token) { Rows++; if (Row is not null) await Row(row); }
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icw-samples-{Guid.NewGuid():N}");
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}

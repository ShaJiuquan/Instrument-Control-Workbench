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
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PpmsPlanImportTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Fact]
    public async Task ActualPpmsFileImportsWithoutIoAndExecutesNestedRoutedRowsThenReopensNativeArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ppms-import-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-voltage-import.json"), b.Bindings, Token);
                Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
                Assert.Equal(3, document.Plan.Metadata["ppms.root.0.0.0.source_line"].Value);
                Assert.Contains("route_matrix", document.Text); Assert.DoesNotContain("meas_type", document.Text);
                var path = Path.Combine(folder, "converted.plan.json"); await b.Workspace.SaveAsync(path, document.Text, Token);
                var native = await b.Workspace.LoadAsync(path, Token); await b.Runs.StartPlanAsync(native.Plan, Token);
                var data = b.Runs.LatestDataSet!; Assert.Equal(6, data.Rows.Count); Assert.Equal(5, data.Instruments.Count); Assert.All(data.Rows, row => Assert.Equal(3, row.Environment.Count));
                Assert.Equal(new[] { 0.0, 0, 0, 0.01, 0.01, 0.01 }, data.Rows.Select(r => r.Values[new("ppms.root.0.axis")].Value));
                Assert.Equal(new[] { -0.01, 0, 0.01, -0.01, 0, 0.01 }, data.Rows.Select(r => r.Values[new("ppms.root.0.0.axis")].Value));
                Assert.All(data.Rows, row => { Assert.True(row.Values.ContainsKey(new("ppms.root.0.0.0.value"))); data.Schema.ValidateRow(row); });
                Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
                var held = await b.Field.GetSnapshotAsync(Token); Assert.False(held.Moving); Assert.Equal(held.Readback, held.Target);
                Assert.False(b.Runs.HasActiveRun); id = data.Id;
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(id, Token);
            Assert.Equal(6, saved.DataSet.Rows.Count); Assert.Equal(3, saved.DataSet.Plan!.Metadata["ppms.root.0.0.0.source_line"].Value);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan, Token); Assert.Equal(6, fresh.Runs.LatestDataSet!.Rows.Count);
            await reopened.ExportCsvAsync(id, Path.Combine(folder, "export.csv"), Token); Assert.Contains("ppms.root.0.0.axis", await File.ReadAllTextAsync(Path.Combine(folder, "export.csv")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task TemperatureFieldVoltageRuntimeFileRunsTwelveRoutedRowsAndReopensForExecution()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ppms-temperature-import-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-temperature-field-voltage-import.json"), b.Bindings, Token);
                Assert.Contains("software_temperature_ramp", document.Text); Assert.Contains("for_each", document.Text);
                Assert.False((await b.Temperature.GetSnapshotAsync(Token)).Connected);
                var nativePath = Path.Combine(folder, "converted.plan.json"); await b.Workspace.SaveAsync(nativePath, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(nativePath, Token)).Plan, Token);
                var data = b.Runs.LatestDataSet!; Assert.Equal(12, data.Rows.Count); Assert.Equal(5, data.Instruments.Count); Assert.All(data.Rows, row => Assert.Equal(3, row.Environment.Count));
                Assert.Equal(new[] { 300.0, 300, 300, 300, 300.01, 300.01, 300.01, 300.01, 300.02, 300.02, 300.02, 300.02 }, data.Rows.Select(r => r.Values[new("ppms.root.0.axis")].Value));
                Assert.All(data.Rows, row => { Assert.Equal(3, row.Values[new("ppms.root.0.axis.total")].Value); data.Schema.ValidateRow(row); });
                var controller = await b.Temperature.GetSnapshotAsync(Token); Assert.Equal(controller.Readback, controller.Target); Assert.False(controller.Moving);
                Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Temperature.ReadControlConfigurationAsync(Token));
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
                id = data.Id;
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var saved = await reopened.LoadAsync(id, Token);
            Assert.Equal(12, saved.DataSet.Rows.Count); Assert.Equal(4, saved.DataSet.Plan!.Metadata["ppms.root.0.0.0.0.source_line"].Value);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan, Token); Assert.Equal(12, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "temperature.csv"); await reopened.ExportCsvAsync(id, csv, Token); Assert.Contains("ppms.root.0.axis", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("software", 5, true)]
    [InlineData("auto", 5, true)]
    [InlineData("auto", 0, true)]
    [InlineData("hardware", 0, true)]
    [InlineData("hardware", 5, false)]
    public async Task TemperatureStrategiesAndAbsentPositiveRateUseExplicitControlSemantics(string strategy, double rate, bool software)
    {
        var b = new Bench(); var tree = Node("command", "set_temp", new() { ["target_k"] = 300.05, ["ramp_strategy"] = strategy, ["rate_k_per_min"] = rate, ["wait_mode"] = "none" });
        var document = b.Workspace.Import(tree.ToJsonString(), b.Bindings);
        Assert.Equal(software, document.Text.Contains("software_temperature_ramp", StringComparison.Ordinal)); await b.Runs.StartPlanAsync(document.Plan, Token);
        Assert.Equal(!software, (await b.Temperature.ReadControlConfigurationAsync(Token)).HardwareRampEnabled);
    }
    [Theory]
    [InlineData("small_rate")]
    [InlineData("large_rate")]
    [InlineData("target")]
    [InlineData("scan_disabled")]
    [InlineData("scan_rate")]
    [InlineData("sampling")]
    public async Task UnsupportedTemperaturePoliciesRejectBeforeConnect(string fault)
    {
        var b = new Bench(); var p = new JsonObject { ["target_k"] = 300, ["wait_mode"] = "none" }; var node = Node("command", "set_temp", p);
        switch (fault)
        {
            case "small_rate": Object(node)["rate_k_per_min"] = 0.001; break;
            case "large_rate": Object(node)["rate_k_per_min"] = 21; break;
            case "target": Object(node)["target_k"] = 311; break;
            case "scan_disabled": node = Node("scan", "scan_temp", new() { ["points"] = new JsonArray(300), ["enable_control"] = false }); break;
            case "scan_rate": node = Node("scan", "scan_temp", new() { ["points"] = new JsonArray(300), ["rate_k_per_min"] = 0 }); break;
            case "sampling": node = Node("scan", "scan_temp", new() { ["points"] = new JsonArray(300), ["sample_mode"] = "continuous" }); break;
        }
        Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings));
        Assert.False((await b.Temperature.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData("set_temp", "target_k", 300)]
    [InlineData("FIELD_SET", "target_t", 0)]
    [InlineData("rotator.set_angle", "target_deg", 0)]
    public async Task EnvironmentalAliasesSetAndReadThroughBoundDevices(string alias, string key, double target)
    {
        var b = new Bench(); var p = new JsonObject { [key] = target, ["wait_mode"] = "none" };
        if (key == "target_k") p["ramp_strategy"] = "hardware";
        var read = key == "target_k" ? "read_temp" : key == "target_t" ? "read_field" : "read_angle";
        var tree = Tree(Node("command", alias, p), Node("measurement", read));
        var document = b.Workspace.Import(tree.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(document.Plan, Token);
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(target, row.Values[new("ppms.root.1.value")].Value);
        Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage, "voltage_set", "target_v", 0.02)]
    [InlineData(SourceFunction.Current, "current_set", "target_a", 1e-6)]
    public async Task SourceSetAliasesPreserveFunctionComplianceOutputAndReadSettings(SourceFunction function, string name, string key, double target)
    {
        var b = new Bench(function: function);
        var set = Node("command", name, new() { [key] = target, ["channel"] = "ch1", ["instrument"] = "k2450" });
        var read = Node("measurement", function == SourceFunction.Voltage ? "meas_current" : "meas_voltage", new() { ["instrument"] = "k2450", ["sensing_mode"] = "4wire", ["nplc"] = 2 });
        var doc = b.Workspace.Import(Tree(set, read).ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(doc.Plan, Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows); var snapshot = await b.Source.GetSnapshotAsync(Token);
        Assert.Equal(function, snapshot.Source.Function); Assert.Equal(target, snapshot.Source.Level); Assert.Equal(2, snapshot.Sense.Nplc);
        Assert.False(snapshot.OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task RepeatAndDelayAliasesRetainChildOrderAndBackendDefaultCycles()
    {
        var b = new Bench(); var body = Tree(Node("delay", "wait_before", new() { ["delay_s"] = 0 }, Node("measurement", "read_field")), Node("command", "sleep", new() { ["seconds"] = 0 }));
        var repeat = Node("loop", "repeat_n", new(), body);
        var doc = b.Workspace.Import(repeat.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(doc.Plan, Token);
        Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.Equal(MeasurementRunState.Completed, b.Runs.LatestOutcome!.State);
    }
    [Theory]
    [InlineData("linear", 3, 0, 1)]
    [InlineData("linear", 1, 0, 0)]
    [InlineData("log", 3, 1, 4)]
    [InlineData("inv_t", 3, 1, 4)]
    [InlineData("inv_t2", 3, 1, 4)]
    public void PointSpecsRetainPpmsEndpointsAndSinglePointSemantics(string mode, int count, double start, double end)
    {
        var b = new Bench(); var spec = new JsonObject { ["mode"] = mode, ["start"] = start, ["end"] = end, ["points"] = count };
        var doc = b.Workspace.Import(Node("scan", "field", new() { ["points"] = spec }).ToJsonString(), b.Bindings);
        var scan = Assert.IsType<ScanStep>(doc.Plan.Root); Assert.Equal(count, scan.Values.Values.Count);
        Assert.Equal(start, scan.Values.Values[0].Value, 12); Assert.Equal(count == 1 ? start : end, scan.Values.Values[^1].Value, 10);
    }
    [Fact]
    public void SignedDescendingStepAndSegmentsFollowPpmsInsteadOfNativeSegmentDedup()
    {
        var b = new Bench(); var p = new JsonObject { ["points"] = new JsonObject { ["mode"] = "step", ["start"] = 1, ["end"] = 0, ["step"] = -0.3 } };
        var scan = Assert.IsType<ScanStep>(b.Workspace.Import(Node("scan", "field", p).ToJsonString(), b.Bindings).Plan.Root);
        Assert.Equal(new[] { 1.0, 0.7, 0.4, 0.1, 0 }, scan.Values.Values.Select(q => Math.Round(q.Value, 10)));
        var segments = new JsonObject { ["segments"] = new JsonArray(new JsonObject { ["mode"] = "list", ["values"] = new JsonArray(0, 1) }, new JsonObject { ["mode"] = "list", ["values"] = new JsonArray(2, 3) }) };
        p["points"] = segments;
        scan = Assert.IsType<ScanStep>(b.Workspace.Import(Node("scan", "field", p).ToJsonString(), b.Bindings).Plan.Root);
        Assert.Equal(new[] { 0.0, 1, 3 }, scan.Values.Values.Select(q => q.Value));
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("children")]
    [InlineData("mode")]
    [InlineData("points")]
    [InlineData("range")]
    [InlineData("route")]
    [InlineData("instrument")]
    [InlineData("function")]
    [InlineData("null")]
    [InlineData("cycles")]
    [InlineData("duplicate")]
    [InlineData("persistent")]
    [InlineData("software_temperature")]
    [InlineData("temperature_scan")]
    [InlineData("source_voltage_limit")]
    public async Task UnsupportedOrInvalidImportedWorkNeverReachesDriver(string damage)
    {
        var b = new Bench(); var node = Node("scan", "scan_voltage", new() { ["points"] = new JsonArray(0.01), ["channel"] = "ch1" }, Node("measurement", "read_field"));
        switch (damage)
        {
            case "unknown": node["type"] = "parallel"; break;
            case "children": node = Node("command", "sleep", new() { ["seconds"] = 0 }, Node("measurement", "read_field")); break;
            case "mode": Object(node)["voltage_mode"] = "unknown"; break;
            case "points": Object(node)["points"] = new JsonObject { ["mode"] = "shuffle", ["start"] = 0, ["end"] = 1, ["points"] = 3 }; break;
            case "range": Object(node)["source_range_v"] = "201"; break;
            case "route": Object(node)["channel"] = "missing"; break;
            case "instrument": node = Node("measurement", "read_voltage", new() { ["instrument"] = "k2182" }); break;
            case "function": node = Node("command", "set_current", new() { ["instrument"] = "k2450", ["target_a"] = 1e-6, ["channel"] = "ch1" }); break;
            case "null": node["params"] = null; break;
            case "cycles": node = Node("repeat", "repeat", new() { ["num_cycles"] = 0 }, Node("measurement", "read_field")); break;
            case "duplicate":
                Assert.Throws<FormatException>(() => b.Workspace.Import("{\"type\":\"sequence\",\"type\":\"command\",\"children\":[]}", b.Bindings)); break;
            case "persistent": node = Node("command", "field_set", new() { ["target_t"] = 0.1, ["mode"] = "persistent" }); break;
            case "temperature_scan": node = Node("scan", "scan_temp", new() { ["points"] = new JsonArray(300), ["scan_mode"] = "continuous" }, Node("measurement", "read_temp")); break;
            case "source_voltage_limit": Object(node)["points"] = new JsonArray(205); break;
            case "software_temperature": node = Node("command", "set_temp", new() { ["target_k"] = 300, ["ramp_strategy"] = "unknown" }); break;
        }
        if (damage != "duplicate")
        {
            node["source_line"] = 9; var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings));
            Assert.Contains("ppms.root", error.Message); Assert.Contains("source line 9", error.Message);
        }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task ImportPreflightRejectsOutOfRangeBeforeIo()
    {
        var b = new Bench(); var payload = Node("command", "voltage_set", new() { ["target_v"] = 220, ["channel"] = "none" });
        Assert.Throws<FormatException>(() => b.Workspace.Import(payload.ToJsonString(), b.Bindings));
        var fieldBindings = new ExperimentImportBindings([], [KeyValuePair.Create("field", b.Field.Definition)]);
        var doc = Node("command", "field_set", new() { ["target_t"] = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Workspace.Import(doc.ToJsonString(), fieldBindings));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task ImportedLongDelayCanPauseAndAbortUsingTheSameRunLifecycle()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var tree = Tree(Node("command", "voltage_set", new() { ["target_v"] = 0.01, ["channel"] = "ch1" }), Node("measurement", "read_field"), Node("command", "sleep", new() { ["seconds"] = 30 }));
        var run = b.Runs.StartPlanAsync(b.Workspace.Import(tree.ToJsonString(), b.Bindings).Plan, Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public void ParsingBudgetsAndBindingSnapshotsRejectAmbiguity()
    {
        var b = new Bench(); var importer = new PpmsExperimentPlanImporter();
        Assert.Throws<FormatException>(() => importer.Parse(new string(' ', JsonExperimentPlanDocument.MaximumBytes + 1), b.Bindings));
        var spec = new JsonObject { ["mode"] = "linear", ["start"] = 0, ["end"] = 1, ["points"] = 1_000_001 };
        Assert.Throws<FormatException>(() => importer.Parse(Node("scan", "field", new() { ["points"] = spec }).ToJsonString(), b.Bindings));
        var list = new Dictionary<string, InstrumentId> { ["k2450"] = b.Source.Id }; var bindings = new ExperimentImportBindings(list, []);
        list["k2450"] = new("other"); Assert.Equal(b.Source.Id, bindings.Instruments["K2450"]);
        Assert.Throws<ArgumentException>(() => new ExperimentImportBindings([KeyValuePair.Create("a", b.Source.Id), KeyValuePair.Create("A", b.Source.Id)], []));
    }
    [Fact]
    public async Task CancelledImportDoesNotTouchTheSourceFileOrDevices()
    {
        var b = new Bench(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-voltage-import.json"), b.Bindings, cancelled.Token));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Theory]
    [InlineData(SourceFunction.Voltage)]
    [InlineData(SourceFunction.Current)]
    public async Task ExplicitSourceConfigurationCommandsKeepTheirUnitsAndTakeEffect(SourceFunction function)
    {
        var b = new Bench(function: function); var sourceName = function == SourceFunction.Voltage ? "voltage" : "current";
        var senseName = function == SourceFunction.Voltage ? "current" : "voltage";
        var value = function == SourceFunction.Voltage ? 0.02 : 1e-6;
        var tree = Tree(Node("command", "k2450.set_source_level", new() { ["function"] = sourceName, ["level"] = value }),
            Node("command", "k2450.set_compliance", new() { ["function"] = sourceName, ["compliance"] = function == SourceFunction.Voltage ? 0.002 : 5 }),
            Node("command", "k2450.set_nplc", new() { ["function"] = senseName, ["nplc"] = 2 }),
            Node("command", "k2450.set_output", new() { ["enabled"] = true }), Node("measurement", "read_field"));
        await b.Runs.StartPlanAsync(b.Workspace.Import(tree.ToJsonString(), b.Bindings).Plan, Token);
        var snapshot = await b.Source.GetSnapshotAsync(Token); Assert.Equal(value, snapshot.Source.Level); Assert.Equal(2, snapshot.Sense.Nplc);
        Assert.Equal(function == SourceFunction.Voltage ? 0.002 : 5, snapshot.Source.Compliance); Assert.False(snapshot.OutputEnabled);
        Assert.Single(b.Runs.LatestDataSet!.Rows);
    }
    [Fact]
    public void RawAndGeneratedTreeBudgetsApplyToAllChildrenBeforeIo()
    {
        var b = new Bench();
        var wide = Tree(Enumerable.Range(0, 10_001).Select(_ => Node("measurement", "read_field")).ToArray());
        Assert.Throws<FormatException>(() => b.Workspace.Import(wide.ToJsonString(), b.Bindings));
        var deep = Node("measurement", "read_field"); for (var i = 0; i < 130; i++) deep = Tree(deep);
        Assert.Throws<FormatException>(() => b.Workspace.Import(deep.ToJsonString(new() { MaxDepth = 512 }), b.Bindings));
        var missing = Node("scan", "field", new() { ["points"] = new JsonObject { ["mode"] = "linear", ["start"] = 0, ["end"] = 1 } });
        Assert.Throws<FormatException>(() => b.Workspace.Import(missing.ToJsonString(), b.Bindings));
        var unsupported = Tree(Node("command", "sleep", new() { ["seconds"] = 0 }), Node("command", "reset_quench"));
        var error = Assert.Throws<FormatException>(() => b.Workspace.Import(unsupported.ToJsonString(), b.Bindings));
        Assert.Contains("ppms.root.1", error.Message);
        var expanded = Tree(Enumerable.Range(0, 2_000).Select(_ => Node("command", "voltage_set", new() { ["target_v"] = 0, ["channel"] = "none" })).ToArray());
        Assert.Throws<FormatException>(() => b.Workspace.Import(expanded.ToJsonString(), b.Bindings));
    }
    [Fact]
    public async Task AnOffOnlyImportedCommandDoesNotApplyAnUnusedPreparation()
    {
        var b = new Bench(); await b.Source.ConnectAsync(Token); await b.Source.ConfigureSourceAsync(new(SourceFunction.Current, 1e-6, 5), Token);
        var doc = b.Workspace.Import(Node("command", "k2450.set_output", new() { ["enabled"] = false }).ToJsonString(), b.Bindings);
        Assert.Empty(doc.Plan.Preparations); await b.Runs.StartPlanAsync(doc.Plan, Token);
        var snapshot = await b.Source.GetSnapshotAsync(Token); Assert.Equal(SourceFunction.Current, snapshot.Source.Function);
        Assert.Equal(1e-6, snapshot.Source.Level); Assert.Equal(5, snapshot.Source.Compliance); Assert.False(snapshot.OutputEnabled);
    }
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private static JsonObject Object(JsonObject node) => (JsonObject)node["params"]!;
    private static JsonObject Tree(params JsonObject[] children) => Node("sequence", "root", null, children);
    private static JsonObject Node(string type, string name, JsonObject? p = null, params JsonObject[] children)
    {
        var node = new JsonObject { ["type"] = type, ["name"] = name, ["params"] = p?.DeepClone() ?? new JsonObject() };
        if (children.Length != 0 || type is "sequence" or "scan") node["children"] = new JsonArray(children.Cast<JsonNode?>().ToArray());
        return node;
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new(); public MockKeithley2450SourceMeter Source { get; } = new();
        public MockAxisController Field { get; } = MockEnvironmentCatalog.Field(); public MockAxisController Temperature { get; } = MockEnvironmentCatalog.Temperature(); public MockMatrixSwitch Matrix { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public ExperimentImportBindings Bindings { get; }
        public Bench(IRunResultListener? archive = null, SourceFunction function = SourceFunction.Voltage)
        {
            var manager = new InstrumentManager(Source, Hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            var temperature = Temperature; var angle = MockEnvironmentCatalog.Angle();
            var descriptor = new DeviceDescriptor(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []);
            Matrix = new(new(descriptor, [new(1, 4, 6)], [Source.Id]));
            var devices = new DeviceRegistry([manager, new AxisControllerSession(Field, Hub), new AxisControllerSession(temperature, Hub), new AxisControllerSession(angle, Hub), new MatrixSwitchSession(Matrix, Hub)], leases);
            Runs = new(manager, leases, Hub, archive is null ? null : [archive], deviceRegistry: devices);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
            Bindings = new([KeyValuePair.Create("k2450", Source.Id)], [KeyValuePair.Create("field", Field.Definition), KeyValuePair.Create("temperature", temperature.Definition), KeyValuePair.Create("angle", angle.Definition)],
                [new(Source.Id, function, new(0, function == SourceFunction.Voltage ? PhysicalUnit.Volt : PhysicalUnit.Ampere), new(function == SourceFunction.Voltage ? 0.001 : 10, function == SourceFunction.Voltage ? PhysicalUnit.Ampere : PhysicalUnit.Volt), new(function == SourceFunction.Voltage ? SenseFunction.Current : SenseFunction.Voltage, SensingMode.FourWire, 1, true))],
                [KeyValuePair.Create("ch1", new ImportChannelRoute(Source.Id, new(new("binding"), descriptor.Id, [new(1, 1, 1), new(1, 2, 2)], TimeSpan.Zero)))]);
        }
    }
}

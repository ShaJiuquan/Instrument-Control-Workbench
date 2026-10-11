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
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PpmsResistanceImportTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    [Fact]
    public async Task SourceManualRangeAliasConfiguresFourWireSampleAcquisition()
    {
        var b = new Bench(); var document = b.Workspace.Import(Measurement("meas_r_sample", new() { ["sample_id"] = "sample1", ["mode"] = "volt_dc", ["voltage_v"] = 0.02, ["range_v"] = "20", ["current_range_a"] = "0.0005", ["settle_s"] = 0 }).ToJsonString(), b.Bindings);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); await b.Runs.StartPlanAsync(document.Plan, Token);
        Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new(20, PhysicalUnit.Volt), (await b.Source.GetSnapshotAsync(Token)).Source.ManualRange);
        Assert.Equal(new(0.001, PhysicalUnit.Ampere), (await b.Source.GetSnapshotAsync(Token)).Sense.ManualRange);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Theory]
    [InlineData("meas_r_2w", "sample4", 1)]
    [InlineData("two_wire", "sample4", 1)]
    [InlineData("sample_r_4w", "sample1", 1)]
    [InlineData("meas_r_sample", "1", 1)]
    [InlineData("sample_r_hallbar", "Hall specimen", 3)]
    [InlineData("rxx_rxy_sample", "PPMS Hall Slot", 3)]
    [InlineData("sample_r_vdp", "sample3", 9)]
    [InlineData("van_der_pauw", "SAMPLE3", 9)]
    public async Task SampleAliasesAcquireCompleteTopologyWithFrozenRoutesAndActualPairs(string name, string sample, int rows)
    {
        var b = new Bench(twoWire: sample == "sample4");
        var document = b.Workspace.Import(Measurement(name, new() { ["sample_id"] = sample, ["mode"] = "volt_dc", ["voltage_v"] = 0.02, ["settle_s"] = 0 }).ToJsonString(), b.Bindings);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.NotNull(document.Plan.Routing);
        await b.Runs.StartPlanAsync(document.Plan, Token); Assert.Equal(rows, b.Runs.LatestDataSet!.Rows.Count);
        var raw = b.Runs.LatestDataSet.Rows.Where(r => r.Routing is not null).ToArray();
        Assert.All(raw, row => { b.Routing.ValidateContext(row.Routing!); Assert.Equal(0.02, row.Values[new("ppms.root.V_V")].Value); Assert.Equal(row.Values[new("ppms.root.V_V")].Value / row.Values[new("ppms.root.I_A")].Value, row.Values[new("ppms.root.R_ohms")].Value); });
        Assert.Equal(raw.Length, b.Source.Reads); Assert.Equal(raw.Length, b.Matrix.Closes);
        if (rows > 1) Assert.NotNull(b.Runs.LatestDataSet.Rows[^1].Analysis);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("resistance")]
    [InlineData("measure_r")]
    [InlineData("res")]
    [InlineData("meas-res")]
    [InlineData("rxx_rxy")]
    public async Task OwnedChannelResistanceUsesVoltageComplianceNplcAliasesAndTurnsOutputOff(string alias)
    {
        var b = new Bench(); var node = Measurement(alias, new()
        {
            ["meas_mode"] = "volt_dc",
            ["bias_v"] = 0.03,
            ["current_limit_a"] = 0.002,
            ["sense_nplc"] = 2,
            ["sense_mode"] = "kelvin",
            ["measure_settle_s"] = 0,
            ["channel"] = "ch1",
            ["source_instrument"] = "K2450",
            ["sense_instrument"] = "k2450"
        });
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings); Assert.Contains("verify_matrix_route", document.Text);
        await b.Runs.StartPlanAsync(document.Plan, Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(0.03, row.Values[new("ppms.root.V_V")].Value); var snapshot = await b.Source.GetSnapshotAsync(Token);
        Assert.Equal(0.002, snapshot.Source.Compliance); Assert.Equal(2, snapshot.Sense.Nplc); Assert.False(snapshot.OutputEnabled);
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.Equal(1, b.Matrix.Closes);
    }
    [Fact]
    public async Task NumericStringAliasesUseInvariantUnitsAndNullOptionsRetainDefaults()
    {
        var b = new Bench(); var node = Measurement("resistance", new()
        {
            ["meas_mode"] = "volt_dc",
            ["voltage"] = "0.03",
            ["compliance"] = "2e-3",
            ["sense_nplc"] = "2",
            ["sense_mode"] = true,
            ["measure_settle_s"] = "0",
            ["channel"] = "ch1",
            ["meter_range_a"] = null,
            ["source_range_v"] = null
        });
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(document.Plan, Token);
        Assert.Equal(0.03, Assert.Single(b.Runs.LatestDataSet!.Rows).Values[new("ppms.root.V_V")].Value);
        var source = await b.Source.GetSnapshotAsync(Token); Assert.Equal(0.002, source.Source.Compliance); Assert.Equal(2, source.Sense.Nplc);
    }
    [Fact]
    public async Task VoltageScanInjectsManagedChannelContextWithoutChangingRequestedLevelsOrSwitchingBetweenPoints()
    {
        var b = new Bench(); var node = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_voltage",
            ["params"] = new JsonObject { ["points"] = new JsonArray(-0.02, 0.01, 0.03), ["channel"] = "ch1", ["compliance_a"] = 0.002, ["settle_s"] = 0 },
            ["children"] = new JsonArray(Measurement("meas_r", new() { ["mode"] = "volt_dc", ["voltage_v"] = 0.1, ["compliance_a"] = 0.0001, ["sensing_mode"] = "4wire", ["settle_s"] = 0 }))
        };
        var document = b.Workspace.Import(node.ToJsonString(), b.Bindings);
        await b.Runs.StartPlanAsync(b.Workspace.Validate(document.Text).Plan, Token);
        Assert.Equal(new[] { -0.02, 0.01, 0.03 }, b.Runs.LatestDataSet!.Rows.Select(r => r.Values[new("ppms.root.0.V_V")].Value));
        Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Equal(row.Values[new("ppms.root.axis")], row.Values[new("ppms.root.0.V_V")]));
        Assert.Equal(1, b.Matrix.Closes); Assert.Equal(0.002, (await b.Source.GetSnapshotAsync(Token)).Source.Compliance);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task SourceScanContextEndsBeforeFollowingOwnedMeasurement()
    {
        var b = new Bench();
        var scan = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_voltage",
            ["params"] = new JsonObject { ["points"] = new JsonArray(0.02), ["channel"] = "ch1", ["settle_s"] = 0 },
            ["children"] = new JsonArray(Measurement("resistance", new() { ["mode"] = "volt_dc", ["sensing_mode"] = true, ["settle_s"] = 0 }))
        };
        var owned = Measurement("resistance", new() { ["mode"] = "volt_dc", ["voltage_v"] = 0.03, ["sensing_mode"] = true, ["channel"] = "ch1", ["settle_s"] = 0 });
        var tree = new JsonObject { ["type"] = "sequence", ["children"] = new JsonArray(scan, owned) };
        var document = b.Workspace.Import(tree.ToJsonString(), b.Bindings); await b.Runs.StartPlanAsync(document.Plan, Token);
        Assert.Equal(0.02, b.Runs.LatestDataSet!.Rows[0].Values[new("ppms.root.0.0.V_V")].Value);
        Assert.Equal(0.03, b.Runs.LatestDataSet.Rows[1].Values[new("ppms.root.1.V_V")].Value);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Fact]
    public async Task ManagedChannelMismatchRefusesRelayChangesAndAcquisition()
    {
        var b = new Bench(); var channels = b.Bindings.Channels.Concat([KeyValuePair.Create("other", new ImportChannelRoute(b.Source.Id,
            new(new("other_route"), b.Routing.Matrix, [new(1, 1, 5), new(1, 2, 6)], TimeSpan.Zero)))]);
        var bindings = new ExperimentImportBindings(b.Bindings.Instruments, b.Bindings.Axes, b.Bindings.Preparations, channels, b.Bindings.Samples);
        var tree = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_voltage",
            ["params"] = new JsonObject { ["points"] = new JsonArray(0.02), ["channel"] = "ch1", ["settle_s"] = 0 },
            ["children"] = new JsonArray(Measurement("resistance", new() { ["mode"] = "volt_dc", ["sensing_mode"] = true, ["channel"] = "other", ["settle_s"] = 0 }))
        };
        var document = b.Workspace.Import(tree.ToJsonString(), bindings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(document.Plan, Token));
        Assert.Equal(0, b.Source.Reads); Assert.Equal(1, b.Matrix.Closes); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task ZeroActualCurrentCannotProduceAnInventedResistance()
    {
        var b = new Bench(); var document = b.Workspace.Import(Measurement("resistance", new()
        {
            ["mode"] = "volt_dc",
            ["sensing_mode"] = true,
            ["channel"] = "ch1",
            ["voltage_v"] = 0,
            ["settle_s"] = 0
        }).ToJsonString(), b.Bindings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(document.Plan, Token));
        Assert.Contains("current is zero", error.Message); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task ExplicitManagedReadRequiresAlreadyEnabledSourceAndDoesNotEnableIt()
    {
        var b = new Bench(); var document = b.Workspace.Import(Measurement("meas_r", new() { ["mode"] = "volt_dc", ["channel"] = "none", ["source_managed_externally"] = true, ["sensing_mode"] = true, ["settle_s"] = 0 }).ToJsonString(), b.Bindings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Runs.StartPlanAsync(document.Plan, Token));
        Assert.Equal(0, b.Source.Reads); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }
    [Theory]
    [InlineData("mode")]
    [InlineData("missing_mode")]
    [InlineData("sample")]
    [InlineData("primitive")]
    [InlineData("sense")]
    [InlineData("role")]
    [InlineData("range")]
    [InlineData("managed")]
    [InlineData("aliases")]
    [InlineData("record")]
    [InlineData("nonfinite")]
    [InlineData("nplc")]
    [InlineData("compliance")]
    [InlineData("voltage")]
    [InlineData("channel")]
    [InlineData("unknown")]
    public async Task InvalidAndUnmigratedResistanceSettingsRejectWithPathBeforeIo(string damage)
    {
        var b = new Bench(); var node = Measurement("meas_r_sample", new() { ["sample_id"] = "sample1", ["mode"] = "volt_dc" }); var p = (JsonObject)node["params"]!;
        switch (damage)
        {
            case "mode": p["mode"] = "curr_delta"; break;
            case "missing_mode": p.Remove("mode"); break;
            case "sample": p["sample_id"] = "missing"; break;
            case "primitive": node["name"] = "meas_r_2w"; break;
            case "sense": p["sensing_mode"] = false; break;
            case "role": p["meter_instrument"] = "k2182"; break;
            case "range": p["source_range_v"] = "201"; break;
            case "managed": p["source_managed"] = true; break;
            case "aliases": p["sample"] = "sample2"; break;
            case "record": p["record_temperature"] = "invalid boolean"; break;
            case "nonfinite": p["voltage_v"] = "NaN"; break;
            case "nplc": p["nplc"] = 11; break;
            case "compliance": p["voltage_v"] = 100; p["compliance_a"] = 1; break;
            case "voltage": p["voltage_v"] = 205; break;
            case "channel": p["channel"] = "ch1"; break;
            case "unknown": p["fake_filter"] = false; break;
        }
        node["source_line"] = 7; var error = Assert.Throws<FormatException>(() => b.Workspace.Import(node.ToJsonString(), b.Bindings));
        Assert.Contains("ppms.root", error.Message); Assert.Contains("source line 7", error.Message);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.Equal(0, b.Matrix.Connects);
    }
    [Fact]
    public async Task SampleUnderItsOwnSourceScanRejectsRatherThanOverwritingBiasAndOwnership()
    {
        var b = new Bench(); var tree = new JsonObject
        {
            ["type"] = "scan",
            ["name"] = "scan_voltage",
            ["params"] = new JsonObject { ["points"] = new JsonArray(0.01), ["channel"] = "ch1" },
            ["children"] = new JsonArray(Measurement("sample_r", new() { ["mode"] = "volt_dc", ["sample_id"] = "sample1" }))
        };
        Assert.Throws<FormatException>(() => b.Workspace.Import(tree.ToJsonString(), b.Bindings)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task SourceEnableThenAcquisitionDwellSupportsPauseAbortAndNoPrematureRow()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var document = b.Workspace.Import(Measurement("sample_r", new() { ["mode"] = "volt_dc", ["sample_id"] = "sample1", ["settle_s"] = 30 }).ToJsonString(), b.Bindings);
        var run = b.Runs.StartPlanAsync(document.Plan, Token); await OutputEnabled(events);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(0.02, PhysicalUnit.Volt), Token));
        b.Runs.Abort(); await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Empty(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestOutcome!.State); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task RouteDriftDuringEnabledAcquisitionDwellCannotProduceASampleRow()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var document = b.Workspace.Import(Measurement("sample_r", new() { ["mode"] = "volt_dc", ["sample_id"] = "sample1", ["settle_s"] = 0.2 }).ToJsonString(), b.Bindings);
        var run = b.Runs.StartPlanAsync(document.Plan, Token); await OutputEnabled(events);
        await b.Matrix.Inner.CloseAsync([new(1, 1, 6)], Token); // Simulates an independent front-panel relay change.
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task FailureOnSecondHallReadPreservesFirstRawRowAndNeverInventsSummary()
    {
        var b = new Bench(); b.Source.FailReadAt = 2;
        var document = b.Workspace.Import(Measurement("hallbar", new() { ["mode"] = "volt_dc", ["sample_id"] = "sample2", ["settle_s"] = 0 }).ToJsonString(), b.Bindings);
        await Assert.ThrowsAsync<IOException>(() => b.Runs.StartPlanAsync(document.Plan, Token));
        var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.NotNull(row.Routing); Assert.Null(row.Analysis);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ReadAndStopFailureRetainOriginalErrorsAndGroupForRecovery()
    {
        var b = new Bench(); b.Source.FailReadAt = 1; b.Source.FailStopAfterRead = true;
        var document = b.Workspace.Import(Measurement("sample_r", new() { ["mode"] = "volt_dc", ["sample_id"] = "sample1", ["settle_s"] = 0 }).ToJsonString(), b.Bindings);
        var error = await Assert.ThrowsAsync<AggregateException>(() => b.Runs.StartPlanAsync(document.Plan, Token));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("read failed", StringComparison.Ordinal));
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("stop failed", StringComparison.Ordinal));
        Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase); Assert.True(b.Runs.HasActiveRun);
        b.Source.FailStopAfterRead = false; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task ActualMixedSampleRuntimeFileExecutesTwentyEightRowsAndReopensNativeArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ppms-sample-import-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive: archive);
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-field-samples-import.json"), b.Bindings, Token);
                var native = Path.Combine(folder, "sample.plan.json"); await b.Workspace.SaveAsync(native, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(native, Token)).Plan, Token);
                var data = b.Runs.LatestDataSet!; Assert.Equal(28, data.Rows.Count); Assert.Equal(26, data.Rows.Count(r => r.MeasurementGroup is null)); Assert.Equal(2, data.Rows.Count(r => r.MeasurementGroup is not null)); Assert.Equal(4, data.Rows.Count(r => r.Analysis is not null));
                var points = data.Rows.Where(r => r.MeasurementGroup is not null).ToArray();
                Assert.All(data.Rows.Where(r => r.Routing is not null), row => Assert.Equal(3, row.Environment.Count));
                Assert.All(data.Rows.Where(r => r.Analysis is not null), row => Assert.Equal(data.Rows[row.Analysis!.FirstInputRow - 1].Environment, row.Environment));
                Assert.Equal(new[] { 1, 15 }, points.Select(p => p.MeasurementGroup!.Inputs[0].FirstInputRow));
                Assert.All(points, point =>
                {
                    Assert.Equal(new[] { 1, 3, 9 }, point.MeasurementGroup!.Inputs.Select(i => i.InputCount));
                    Assert.Equal(9, point.Environment.Count);
                    var first = point.MeasurementGroup.Inputs[0].FirstInputRow - 1;
                    Assert.Equal(data.Rows[first].Timestamp, point.Timestamp);
                    Assert.Equal(data.Rows[first].Values[new("elapsed")], point.Values[new("elapsed")]);
                    foreach (var member in point.MeasurementGroup.Inputs)
                        foreach (var value in data.Rows[member.FirstInputRow + member.InputCount - 2].Values.Where(v => v.Key.Value != "elapsed"))
                            Assert.Equal(value.Value, point.Values[value.Key]);
                });
                Assert.Equal(new[] { "sample1", "sample2", "sample3" }, data.Rows.Where(r => r.Routing is not null).Select(r => r.Routing!.Binding.Sample.Value).Distinct());
                Assert.All(data.Rows, row => data.Schema.ValidateRow(row)); Assert.Equal(3, data.Instruments.Count);
                Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); id = data.Id;
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(id, Token);
            Assert.Equal(28, stored.DataSet.Rows.Count); Assert.Equal(4, stored.DataSet.Plan!.Metadata["ppms.root.0.2.source_line"].Value);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan!, Token); Assert.Equal(28, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "sample.csv"); await reopened.ExportCsvAsync(id, csv, Token); var text = await File.ReadAllTextAsync(csv);
            Assert.Contains("routing_json", text); Assert.Contains("analysis_json", text); Assert.Contains("measurement_group_json", text); Assert.Contains("HallLongitudinal", text); Assert.Contains("VdpCbAd", text);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static JsonObject Measurement(string name, JsonObject p) => new() { ["type"] = "measurement", ["name"] = name, ["params"] = p };
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != kind) { }
    }
    private static async Task OutputEnabled(NotificationSubscription events)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await events.Reader.ReadAsync(deadline.Token);
            if (message.Snapshot?.OutputEnabled == true) return;
        }
    }
    private sealed class Bench
    {
        public SourceProbe Source { get; } = new(); public MatrixProbe Matrix { get; }
        public NotificationHub Hub { get; } = new(); public DeviceRegistry Registry { get; }
        public RoutingConfiguration Routing { get; }
        public ExperimentImportBindings Bindings { get; }
        public MeasurementRunService Runs { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench(bool twoWire = false, IRunResultListener? archive = null)
        {
            var ports = twoWire ? new[] { new MatrixPort(1, 1), new(1, 2), new(1, 1), new(1, 2) } : new[] { new MatrixPort(1, 1), new(1, 2), new(1, 3), new(1, 4) };
            var channel = new ChannelProfile(new("channel1"), "Channel", Enumerable.Range(1, 6).Select(i => new ChannelConnection(new("terminal" + i), 1, i)));
            var samples = Enum.GetValues<SampleTopology>().Select(topology => new SampleProfile(new(topology switch { SampleTopology.FourWire => "sample1", SampleTopology.HallBar => "sample2", SampleTopology.VanDerPauw => "sample3", _ => "sample4" }),
                topology == SampleTopology.HallBar ? "Hall specimen" : topology.ToString(), topology,
                SampleProfile.RequiredLabels(topology).Select((label, i) => new SampleElectrode(new(label), channel.Id, channel.Connections[i].Key)), new(new(0.001, PhysicalUnit.Meter), new(0.002, PhysicalUnit.Meter), new(1e-6, PhysicalUnit.Meter)))).ToArray();
            var wiring = new InstrumentWiringProfile(new("smu"), Source.Id, ports[0], ports[1], ports[2], ports[3]);
            Routing = new(new("matrix"), [channel], [wiring], samples);
            var matrixDescriptor = new DeviceDescriptor(Routing.Matrix, "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []);
            Matrix = new(new(matrixDescriptor, [new(1, 4, 6)], [Source.Id]), Source);
            var manager = new InstrumentManager(Source, Hub); var field = MockEnvironmentCatalog.Field(); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
            Registry = new([manager, new MatrixSwitchSession(Matrix, Hub), new AxisControllerSession(field, Hub)], leases);
            Runs = new(manager, leases, Hub, archive is null ? null : [archive], deviceRegistry: Registry);
            var preparation = new SourcePreparation(Source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(1e-4, PhysicalUnit.Ampere), new(SenseFunction.Current, twoWire ? SensingMode.TwoWire : SensingMode.FourWire, 1, true));
            Bindings = new([KeyValuePair.Create("k2450", Source.Id)], [KeyValuePair.Create("field", field.Definition)], [preparation],
                [KeyValuePair.Create("ch1", new ImportChannelRoute(Source.Id, new(new("route"), Routing.Matrix, [new(1, 1, 1), new(1, 2, 2), new(1, 3, 3), new(1, 4, 4)], TimeSpan.Zero)))],
                new(Routing, [KeyValuePair.Create("volt_dc", wiring.Id)], [KeyValuePair.Create("PPMS Hall Slot", new SampleId("sample2"))]));
            Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
        }
    }
    private sealed class SourceProbe : ISourceMeterPort
    {
        private readonly MockKeithley2450SourceMeter inner = new();
        public int Reads { get; private set; }
        public int? FailReadAt { get; set; }
        public bool FailStopAfterRead { get; set; }
        public InstrumentId Id => inner.Id; public string DisplayName => inner.DisplayName; public DeviceDescriptor Descriptor => inner.Descriptor;
        public Task<SourceMeterSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task ConfigureSourceAsync(SourceSetpoint value, CancellationToken token) => inner.ConfigureSourceAsync(value, token);
        public Task ConfigureSenseAsync(SenseConfiguration value, CancellationToken token) => inner.ConfigureSenseAsync(value, token);
        public Task EnableOutputAsync(CancellationToken token) => inner.EnableOutputAsync(token);
        public Task DisableOutputAsync(CancellationToken token) => inner.DisableOutputAsync(token);
        public Task<MeasurementValue> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<SourceSenseReading> ReadSourceSenseAsync(CancellationToken token)
        { if (++Reads == FailReadAt) throw new IOException("resistance read failed"); return inner.ReadSourceSenseAsync(token); }
        public Task<SourceMeterSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task<SourceMeterSnapshot> HeartbeatAsync(CancellationToken token) => inner.HeartbeatAsync(token);
        public Task SafeStopAsync(CancellationToken token)
        { if (FailStopAfterRead && Reads > 0) throw new IOException("source stop failed"); return inner.SafeStopAsync(token); }
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
    }
    private sealed class MatrixProbe(MatrixSwitchDefinition definition, SourceProbe source) : IMatrixSwitchPort
    {
        public MockMatrixSwitch Inner { get; } = new(definition); public int Closes { get; private set; }
        public int Connects { get; private set; }
        public MatrixSwitchDefinition Definition => definition;
        public Task<MatrixSwitchSnapshot> ConnectAsync(CancellationToken token) { Connects++; return Inner.ConnectAsync(token); }
        public Task<MatrixSwitchSnapshot> GetSnapshotAsync(CancellationToken token) => Inner.GetSnapshotAsync(token);
        public Task DisconnectAsync(CancellationToken token) => Inner.DisconnectAsync(token);
        public async Task OpenAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        { Assert.False((await source.GetSnapshotAsync(token)).OutputEnabled); await Inner.OpenAsync(points, token); }
        public async Task CloseAsync(IReadOnlyList<MatrixCrossPoint> points, CancellationToken token)
        { Assert.False((await source.GetSnapshotAsync(token)).OutputEnabled); Closes++; await Inner.CloseAsync(points, token); }
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PpmsRoutingProfileImportTests
{
    private static JsonObject GateScanNode(string name = "scan_gate", string range = "source_range_v", string? channel = "ch1")
    {
        var parameters = new JsonObject { ["points"] = new JsonArray(-.02, 0, .02), [range] = "auto", ["compliance_a"] = .0001, ["settle_s"] = 0 };
        if (channel is not null) parameters["channel"] = channel;
        return new()
        {
            ["type"] = "scan",
            ["name"] = name,
            ["source_line"] = 7,
            ["params"] = parameters,
            ["children"] = new JsonArray(new JsonObject { ["type"] = "measurement", ["name"] = "measure_current", ["params"] = new JsonObject { ["instrument"] = "k2450", ["sensing_mode"] = "2wire" } })
        };
    }
    [Theory]
    [InlineData("scan_gate", "source_range_v", "ch1")]
    [InlineData("gate", "range_v", "main contacts")]
    [InlineData("scan_bias", "voltage_range_v", "__active__")]
    [InlineData("bias", "source_range_v", null)]
    public async Task GateScanAliasesInheritActualGateRouteAndCaptureAllSignedPoints(string name, string range, string? channel)
    {
        var b = new Bench(twoWire: true); var doc = b.Workspace.Import(GateScanNode(name, range, channel).ToJsonString(), b.Bind(await b.LoadAsync()));
        var steps = Assert.IsType<SequenceStep>(doc.Plan.Root).Children; var route = Assert.Single(steps.OfType<RouteMatrixStep>()); var scan = Assert.Single(steps.OfType<ScanStep>());
        Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 5), new(1, 2, 6) }, route.Closed);
        var body = Assert.IsType<SequenceStep>(scan.Body); var meter = Assert.IsType<SequenceStep>(body.Children.Last());
        var read = Assert.Single(meter.Children.OfType<ReadStep>()); Assert.Equal(route.Closed, read.RequiredRoute!.Closed); Assert.Equal(b.Source.Id, read.ExcitationSource); Assert.DoesNotContain(meter.Children, step => step is RouteMatrixStep);
        Assert.Contains(body.Children, step => step is AssignValueStep assignment && assignment.Id.Value.EndsWith(".publish.gate", StringComparison.Ordinal));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); await b.Runs.StartPlanAsync(doc.Plan, Token);
        Assert.Equal(new[] { -.02, 0, .02 }, b.Runs.LatestDataSet!.Rows.Select(row => row.Values[new("ppms.root.axis")].Value));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task SourceDrainBiasScanRetainsExistingSourceContacts()
    {
        var b = new Bench(twoWire: true); var node = GateScanNode("scan_voltage", "range_v"); node["params"]!["voltage_mode"] = "bias";
        var doc = b.Workspace.Import(node.ToJsonString(), b.Bind(await b.LoadAsync())); var route = Assert.Single(((SequenceStep)doc.Plan.Root).Children.OfType<RouteMatrixStep>());
        Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 1), new(1, 2, 2), new(1, 3, 3), new(1, 4, 4) }, route.Closed);
        await b.Runs.StartPlanAsync(doc.Plan, Token); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task ExplicitExternalGateScanPreservesFourWireAndDoesNotCreateMatrixOwnership()
    {
        var b = new Bench(); var node = GateScanNode(channel: "none"); node["children"]![0]!["params"]!["sensing_mode"] = "4wire";
        var doc = b.Workspace.Import(node.ToJsonString(), b.Devices); Assert.Null(doc.Plan.Routing);
        await b.Runs.StartPlanAsync(doc.Plan, Token); Assert.Equal(3, b.Runs.LatestDataSet!.Rows.Count); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData("fourwire")]
    [InlineData("missing-profile")]
    [InlineData("duplicate-range")]
    [InlineData("mode")]
    [InlineData("voltage-gate-mode")]
    [InlineData("point")]
    [InlineData("empty-points")]
    [InlineData("compliance")]
    [InlineData("fixed-range")]
    [InlineData("unknown-instrument")]
    [InlineData("child-bypass")]
    [InlineData("child-channel")]
    [InlineData("resistance-child")]
    [InlineData("nested-gate")]
    [InlineData("nested-source")]
    public async Task InvalidGateScanRejectsBeforeAnyConnectionOrOutput(string damage)
    {
        var b = new Bench(twoWire: damage != "fourwire"); var node = GateScanNode(); var p = node["params"]!;
        switch (damage)
        {
            case "duplicate-range": p["range_v"] = "auto"; break;
            case "mode": p["voltage_mode"] = "unknown"; break;
            case "voltage-gate-mode": node["name"] = "scan_voltage"; p["voltage_mode"] = "gate"; break;
            case "point": p["points"]![2] = 201; break;
            case "empty-points": p["points"] = new JsonArray(); break;
            case "compliance": p["compliance_a"] = 2; break;
            case "fixed-range": p["source_range_v"] = .02; p["points"]![2] = 20; break;
            case "unknown-instrument": p["instrument"] = "missing"; break;
            case "child-bypass": node["children"]![0]!["params"]!["channel"] = "none"; break;
            case "child-channel": node["children"]![0]!["params"]!["channel"] = "ch2"; break;
            case "resistance-child": node["children"] = new JsonArray(JsonNode.Parse(Measurement("ch1", fourWire: false))); break;
            case "nested-gate": node["children"] = new JsonArray(GateScanNode()); break;
            case "nested-source": node["children"] = new JsonArray(GateScanNode("scan_voltage")); break;
        }
        var bindings = damage == "missing-profile" ? b.Devices : b.Bind(await b.LoadAsync());
        Assert.ThrowsAny<Exception>(() => b.Workspace.Import(node.ToJsonString(), bindings));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task GateScanPauseKeepsGateRouteAndAbortReleasesSourceAndMatrix()
    {
        var b = new Bench(twoWire: true); var node = GateScanNode(); node["params"]!["settle_s"] = 30;
        var doc = b.Workspace.Import(node.ToJsonString(), b.Bind(await b.LoadAsync())); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(doc.Plan, Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.StepStarted || !(await b.Source.GetSnapshotAsync(deadline.Token)).OutputEnabled) { }
        b.Runs.Pause(); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 5), new(1, 2, 6) }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        b.Runs.Resume(); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunResumed) { }
        Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task GateRouteDriftBeforeReadFaultsWithoutPublishingAnAcquisitionAndCleansUp()
    {
        var b = new Bench(twoWire: true); var node = GateScanNode(); node["params"]!["settle_s"] = .3;
        var doc = b.Workspace.Import(node.ToJsonString(), b.Bind(await b.LoadAsync())); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(doc.Plan, Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.StepStarted || !(await b.Source.GetSnapshotAsync(deadline.Token)).OutputEnabled) { }
        await b.Matrix.OpenAsync([new(1, 1, 5)], deadline.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False(b.Runs.LatestBlackboardSnapshot!.Entries.ContainsKey(new("ppms.root.0.value")));
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task GateScanInsideIterationCapturesBothAxesAndDoesNotLeakGateScopeIntoLaterSourceScan()
    {
        var b = new Bench(twoWire: true);
        var iteration = new JsonObject { ["type"] = "scan", ["name"] = "scan_number", ["params"] = new JsonObject { ["points"] = new JsonArray(1, 2) }, ["children"] = new JsonArray(GateScanNode()) };
        var node = new JsonObject { ["type"] = "sequence", ["children"] = new JsonArray(iteration, GateScanNode("scan_voltage")) };
        var doc = b.Workspace.Import(node.ToJsonString(), b.Bind(await b.LoadAsync())); await b.Runs.StartPlanAsync(doc.Plan, Token);
        var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(9, rows.Count);
        Assert.Equal(new[] { 1d, 1, 1, 2, 2, 2 }, rows.Take(6).Select(row => row.Values[new("ppms.root.0.axis")].Value));
        Assert.Equal(new[] { -.02, 0, .02, -.02, 0, .02 }, rows.Take(6).Select(row => row.Values[new("ppms.root.0.0.axis")].Value));
        Assert.Equal(new[] { -.02, 0, .02 }, rows.Skip(6).Select(row => row.Values[new("ppms.root.1.axis")].Value));
        Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task GateScanSaveFreshRunAndArchivePreserveSignedAxisAndSourceMetadata()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gate-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(twoWire: true, archive: archive); var imported = b.Workspace.Import(GateScanNode("scan_bias").ToJsonString(), b.Bind(await b.LoadAsync()));
            var path = Path.Combine(folder, "native.json"); await b.Workspace.SaveAsync(path, imported.Text, Token); var runtime = new Bench(twoWire: true, archive: archive); var loaded = await runtime.Workspace.LoadAsync(path, Token); await runtime.Runs.StartPlanAsync(loaded.Plan, Token);
            var id = runtime.Runs.LatestDataSet!.Id; await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await fresh.LoadAsync(id, Token);
            Assert.Equal(new[] { -.02, 0, .02 }, stored.DataSet.Rows.Select(row => row.Values[new("ppms.root.axis")].Value)); Assert.NotNull(stored.DataSet.Plan!.Routing); Assert.Contains("source_line", loaded.Text);
            await fresh.ExportCsvAsync(id, Path.Combine(folder, "export.csv"), Token); Assert.Contains("ppms.root.axis", await File.ReadAllTextAsync(Path.Combine(folder, "export.csv"))); Assert.False(runtime.Runs.HasActiveRun); Assert.Empty((await runtime.Matrix.GetSnapshotAsync(Token)).Closed);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

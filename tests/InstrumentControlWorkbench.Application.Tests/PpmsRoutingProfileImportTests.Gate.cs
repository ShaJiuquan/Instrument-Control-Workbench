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
    private static string GateNode(string name = "set_gate", string mode = "source", string target = "target_v", string channel = "ch1") => new JsonObject
    { ["type"] = "command", ["name"] = name, ["source_line"] = 4, ["params"] = new JsonObject { [target] = .02, ["channel"] = channel, ["voltage_mode"] = mode, ["range_v"] = "auto", ["compliance_a"] = .0001 } }.ToJsonString();
    [Theory]
    [InlineData("set_bias", "source", "bias_v", true)]
    [InlineData("bias_set", "source", "voltage_v", true)]
    [InlineData("set_gate", "source", "target_v", true)]
    [InlineData("gate_set", "source", "target_v", true)]
    [InlineData("set_voltage", "gate", "target_v", true)]
    [InlineData("set_voltage", "bias", "target_v", false)]
    public async Task ImportedVoltageModesSelectCorrectTerminalsAndCompleteExistingCleanup(string command, string mode, string target, bool gate)
    {
        var b = new Bench(twoWire: true); var profiles = await b.LoadAsync(); var document = b.Workspace.Import(GateNode(command, mode, target), b.Bind(profiles));
        var steps = Assert.IsType<SequenceStep>(document.Plan.Root).Children; var route = Assert.Single(steps.OfType<RouteMatrixStep>());
        Assert.Equal(gate ? new[] { new MatrixCrossPoint(1, 1, 5), new(1, 2, 6) } : new[] { new MatrixCrossPoint(1, 1, 1), new(1, 2, 2), new(1, 3, 3), new(1, 4, 4) }, route.Closed);
        Assert.IsType<SourceOutputStep>(steps[0]); Assert.False(((SourceOutputStep)steps[0]).Enabled); Assert.Equal(.02, ((ConstantValue)steps.OfType<SetParameterStep>().Last().Value).Value.Value);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); await b.Runs.StartPlanAsync(document.Plan, Token);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task ExplicitExternalGateDoesNotInventARouteAndRetainsFourWirePreparation()
    {
        var b = new Bench(); var document = b.Workspace.Import(GateNode(channel: "none"), b.Devices); Assert.Null(document.Plan.Routing);
        Assert.DoesNotContain(((SequenceStep)document.Plan.Root).Children, step => step is RouteMatrixStep); await b.Runs.StartPlanAsync(document.Plan, Token); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData("fourwire")]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("disabled")]
    [InlineData("duplicate-target")]
    [InlineData("duplicate-range")]
    [InlineData("mode")]
    [InlineData("target")]
    [InlineData("compliance")]
    public async Task InvalidGateDraftRejectsBeforeConnectionAndMatrixMutation(string damage)
    {
        var b = new Bench(twoWire: damage != "fourwire"); var d = Documents(); var connections = (JsonArray)d.Channels["profiles"]!["channel1"]!["connections"]!;
        if (damage is "missing" or "partial") foreach (var connection in connections.OfType<JsonObject>().Where(connection => connection["key"]!.GetValue<string>() == "bias_plus" || damage == "missing" && connection["key"]!.GetValue<string>() == "bias_minus").ToArray()) connections.Remove(connection);
        if (damage == "disabled") connections.OfType<JsonObject>().Single(connection => connection["key"]!.GetValue<string>() == "bias_minus")["enabled"] = false;
        var node = JsonNode.Parse(GateNode())!; var parameters = node["params"]!;
        if (damage == "duplicate-target") parameters["bias_v"] = .02;
        if (damage == "duplicate-range") parameters["source_range_v"] = "auto";
        if (damage == "mode") parameters["voltage_mode"] = "unknown";
        if (damage == "target") parameters["target_v"] = 201;
        if (damage == "compliance") parameters["compliance_a"] = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => b.Workspace.Import(node.ToJsonString(), b.Bind(Parse(d))))); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Fact]
    public async Task GatePauseRetainsRouteWithOutputOffResumeRestoresAndAbortReleasesTheBaseline()
    {
        var b = new Bench(twoWire: true); var node = new JsonObject { ["type"] = "sequence", ["children"] = new JsonArray(JsonNode.Parse(GateNode()), new JsonObject { ["type"] = "command", ["name"] = "wait", ["params"] = new JsonObject { ["seconds"] = 30 } }) };
        var doc = b.Workspace.Import(node.ToJsonString(), b.Bind(await b.LoadAsync())); using var events = b.Hub.Subscribe(); var running = b.Runs.StartPlanAsync(doc.Plan, Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.StepStarted || !(await b.Source.GetSnapshotAsync(deadline.Token)).OutputEnabled) { }
        b.Runs.Pause(); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunPaused) { }
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Equal(new[] { new MatrixCrossPoint(1, 1, 5), new(1, 2, 6) }, (await b.Matrix.GetSnapshotAsync(Token)).Closed);
        b.Runs.Resume(); while ((await events.Reader.ReadAsync(deadline.Token)).Kind != WorkbenchNotificationKind.RunResumed) { }
        Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); b.Runs.Abort(); await running.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(b.Runs.HasActiveRun); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
    }

    [Fact]
    public async Task GateCommandsSaveReloadRunAndFreshArchivePreservesSourceReadAndRoutingProfile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gate-routing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(twoWire: true, archive: archive);
            var node = new JsonObject { ["type"] = "sequence", ["children"] = new JsonArray(JsonNode.Parse(GateNode()), new JsonObject { ["type"] = "measurement", ["name"] = "measure_current", ["params"] = new JsonObject { ["meas_type"] = "basic", ["name"] = "current", ["instrument"] = "k2450", ["channel"] = "none", ["sensing_mode"] = "2wire" } }) };
            var imported = b.Workspace.Import(node.ToJsonString(), b.Bind(await b.LoadAsync())); var path = Path.Combine(folder, "native.json"); await b.Workspace.SaveAsync(path, imported.Text, Token); var freshBench = new Bench(twoWire: true, archive: archive); var loaded = await freshBench.Workspace.LoadAsync(path, Token); await freshBench.Runs.StartPlanAsync(loaded.Plan, Token);
            var id = freshBench.Runs.LatestDataSet!.Id; Assert.Single(freshBench.Runs.LatestDataSet.Rows); Assert.False((await freshBench.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await freshBench.Matrix.GetSnapshotAsync(Token)).Closed);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var persisted = await fresh.LoadAsync(id, Token); Assert.Single(persisted.DataSet.Rows); Assert.Contains("bias_plus", loaded.Text); Assert.NotNull(persisted.DataSet.Plan!.Routing); await fresh.ExportCsvAsync(id, Path.Combine(folder, "export.csv"), Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

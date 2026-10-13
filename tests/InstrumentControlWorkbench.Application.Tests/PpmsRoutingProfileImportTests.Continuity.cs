using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PpmsRoutingProfileImportTests
{
    [Theory]
    [InlineData("electrode_test")]
    [InlineData("test_electrodes")]
    [InlineData("continuity_test")]
    public async Task AllChannelPairsUseTwoWireForcePortsAndPersistTerminalCounts(string name)
    {
        var b = new Bench(twoWire: true); var bindings = b.Bind(await b.LoadAsync());
        var node = new JsonObject { ["type"] = "measurement", ["name"] = name, ["params"] = new JsonObject { ["settle_s"] = 0 } };
        var imported = b.Workspace.Import(node.ToJsonString(), bindings); var probes = Assert.IsType<SequenceStep>(imported.Plan.Root).Children.OfType<ReadElectrodeContinuityStep>().ToArray();
        Assert.Equal(8, probes.Length); Assert.Equal(Enum.GetValues<ElectrodePair>(), probes.Take(4).Select(p => p.Pair)); Assert.All(probes, p => Assert.Equal(Core.Instruments.SensingMode.TwoWire, p.Source.Sense.Mode));
        Assert.Equal(new[] { new Core.Devices.MatrixCrossPoint(1, 1, 3), new(1, 2, 4) }, probes[1].RequiredRoute!.Closed);
        await b.Runs.StartPlanAsync(imported.Plan, Token); Assert.Equal(8, b.Runs.LatestDataSet!.Rows.Count); var bb = b.Runs.LatestBlackboard!;
        double Count(string key) => Assert.IsType<BlackboardQuantity>(bb[new("ppms.root." + key)].Value).Value.Value;
        Assert.Equal(8, Count("pair_count")); Assert.Equal(Count("connected_count") + Count("open_count"), Count("tested_count")); Assert.Equal(8, Count("tested_count") + Count("not_configured_count")); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed);
    }
    [Theory]
    [InlineData("voltage_v", 211)]
    [InlineData("compliance_a", 0)]
    [InlineData("max_connected_ohm", 0)]
    [InlineData("nplc", 11)]
    [InlineData("settle_s", -1)]
    public async Task InvalidDiagnosticParametersPreservePriorPlanAndRemainDisconnected(string key, double value)
    {
        var b = new Bench(twoWire: true); var bindings = b.Bind(await b.LoadAsync()); var node = new JsonObject { ["type"] = "measurement", ["name"] = "electrode_test", ["params"] = new JsonObject { [key] = value } };
        Assert.ThrowsAny<Exception>(() => b.Workspace.Import(node.ToJsonString(), bindings)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Matrix.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task FourWirePreparationCannotSilentlyBecomeTwoWireDiagnostics()
    {
        var b = new Bench(); var bindings = b.Bind(await b.LoadAsync()); Assert.ThrowsAny<Exception>(() => b.Workspace.Import("{\"type\":\"measurement\",\"name\":\"electrode_test\"}", bindings)); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }
}

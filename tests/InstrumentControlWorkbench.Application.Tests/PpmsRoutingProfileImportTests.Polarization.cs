using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PpmsRoutingProfileImportTests
{
    [Theory]
    [InlineData("ch1")]
    [InlineData("1")]
    public async Task FourWirePolarizationUsesFrozenDistinctSourceSenseContactsAndCleansAddedRoutes(string channel)
    {
        var b = new Bench(); var bindings = b.Bind(await b.LoadAsync());
        var node = new JsonObject { ["type"] = "measurement", ["name"] = "polread", ["params"] = new JsonObject { ["channel"] = channel, ["pol_voltage_v"] = 5, ["read_voltage_v"] = 1, ["pol_duration_s"] = 0, ["read_delay_s"] = 0, ["sensing_mode"] = "4wire" } };
        var imported = b.Workspace.Import(node.ToJsonString(), bindings); var pulse = Assert.IsType<ReadPolarizationStep>(Assert.IsType<SequenceStep>(imported.Plan.Root).Children[^1]);
        Assert.NotNull(imported.Plan.Routing); Assert.Equal(4, pulse.RequiredRoute!.Closed.Count);
        await b.Runs.StartPlanAsync(imported.Plan, Token); var row = Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(new(1, PhysicalUnit.Volt), row.Values[pulse.VoltageColumn]);
        Assert.Equal(new(1, PhysicalUnit.Dimensionless), row.Values[pulse.AttemptsColumn]); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.Empty((await b.Matrix.GetSnapshotAsync(Token)).Closed); Assert.False(b.Runs.HasActiveRun);
    }
}

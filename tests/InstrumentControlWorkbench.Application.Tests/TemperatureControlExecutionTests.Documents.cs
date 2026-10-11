using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class TemperatureControlExecutionTests
{
    [Theory]
    [InlineData("rate")]
    [InlineData("rate_k_per_min")]
    public async Task PpmsRampOnAliasDoesNotEnableDisabledControl(string alias)
    {
        var b = new Bench(); await b.Devices.ConnectAsync(b.Id, Token);
        await b.Port.Mock.ConfigureControlAsync(new(false, false), Rate(5), Token);
        var node = Node("temp_ramp_on", JsonNode.Parse($"{{\"{alias}\":3}}")!.AsObject());
        var plan = new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), b.Bindings);
        Assert.Empty(plan.Preparations); Assert.Equal(17, plan.Metadata["ppms.root.source_line"].Value);
        var codec = new JsonExperimentPlanDocument(); var decoded = codec.Parse(codec.Serialize(plan));
        await b.Runs.StartPlanAsync(decoded, Token);
        Assert.Equal(new TemperatureControlConfiguration(true, false), await b.Port.Mock.ReadControlConfigurationAsync(Token));
        Assert.Equal(Rate(3), (await b.Port.Mock.GetSnapshotAsync(Token)).Rate); Assert.Empty(b.Runs.LatestDataSet!.Rows);
    }

    [Theory]
    [InlineData("temp_ramp_on", "{}")]
    [InlineData("temp_ramp_on", "{\"rate\":0}")]
    [InlineData("temp_ramp_on", "{\"rate\":21}")]
    [InlineData("temp_ramp_on", "{\"rate\":\"3\"}")]
    [InlineData("temp_ramp_on", "{\"rate\":3,\"rate_k_per_min\":3}")]
    [InlineData("temp_ramp_off", "{\"rate\":3}")]
    [InlineData("temp_control_on", "{\"enabled\":false}")]
    [InlineData("temp_control_off", "{\"target_k\":305}")]
    public void InvalidImportRejectsWithSourceLineBeforeIo(string name, string parameters)
    {
        var b = new Bench(); var node = Node(name, JsonNode.Parse(parameters)!.AsObject());
        var error = Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), b.Bindings));
        Assert.Contains("17", error.Message); Assert.Equal(0, b.Port.Applies); Assert.Equal(0, b.Port.Holds);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("numeric")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("unused")]
    [InlineData("null")]
    public void InvalidNativeCommandRejects(string kind)
    {
        var b = new Bench(); var codec = new JsonExperimentPlanDocument(); var node = JsonNode.Parse(codec.Serialize(b.Plan(TemperatureControlAction.RampOn)))!.AsObject();
        var command = node["root"]!.AsObject();
        switch (kind)
        {
            case "unknown": command["action"] = "Unknown"; break;
            case "numeric": command["action"] = 0; break;
            case "extra": command["enabled"] = true; break;
            case "missing": command.Remove("rate"); break;
            case "unused": command["action"] = "ControlOff"; break;
            case "null": command["rate"] = null; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(node.ToJsonString())); Assert.Equal(0, b.Port.Applies);
    }

    [Fact]
    public async Task PublishedFixtureRetainsCommandsSourceLinesActualReadbacksAndFreshArchiveReplay()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "InstrumentControlWorkbench.sln"))) root = Directory.GetParent(root)!.FullName;
        var text = await File.ReadAllTextAsync(Path.Combine(root, "examples/plans/ppms-temperature-controls-import.json"));
        var folder = Path.Combine(Path.GetTempPath(), $"temperature-controls-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive); var plan = new PpmsExperimentPlanImporter().Parse(text, b.Bindings);
                var codec = new JsonExperimentPlanDocument(); var path = Path.Combine(folder, "temperature.plan.json");
                await codec.SaveAsync(path, plan, Token); var decoded = await codec.LoadAsync(path, Token);
                await b.Runs.StartPlanAsync(decoded, Token); id = b.Runs.LatestDataSet!.Id;
                Assert.Equal(2, b.Runs.LatestDataSet.Rows.Count);
                Assert.All(b.Runs.LatestDataSet.Rows, row => Assert.Contains(K(300), row.Values.Values));
                Assert.Equal(new TemperatureControlConfiguration(false, true), await b.Port.Mock.ReadControlConfigurationAsync(Token));
                Assert.Equal(4, b.Port.Applies); Assert.Equal(Rate(3), (await b.Port.Mock.GetSnapshotAsync(Token)).Rate);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs")); var stored = await reopened.LoadAsync(id, Token);
            Assert.Equal(2, stored.DataSet.Rows.Count); Assert.Equal(6, stored.DataSet.Plan!.Metadata["ppms.root.5.source_line"].Value);
            var native = new JsonExperimentPlanDocument().Serialize(stored.DataSet.Plan); Assert.Contains("temperature_control", native); Assert.Contains("RampOff", native);
            Assert.Equal("True", Assert.Single(stored.DataSet.Instruments).Configuration["temperature_control_enabled"]);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(stored.DataSet.Plan, Token); Assert.Equal(2, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "temperature.csv"); await reopened.ExportCsvAsync(id, csv, Token);
            var output = await File.ReadAllTextAsync(csv); Assert.Contains("Kelvin", output); Assert.Contains("temperature_control", output);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static JsonObject Node(string name, JsonObject parameters) => new()
    {
        ["type"] = "command",
        ["name"] = name,
        ["source_line"] = 17,
        ["params"] = parameters,
    };
}

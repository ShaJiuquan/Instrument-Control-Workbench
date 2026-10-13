using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class TemperatureCoolingDocumentsTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static string Node(string name, string sample = "linear", string? scanMode = null) => new JsonObject
    {
        ["type"] = "scan",
        ["name"] = name,
        ["params"] = new JsonObject { ["points"] = new JsonArray(4, 3.9, 3.8), ["cooling_setpoint_k"] = 3.8, ["sample_mode"] = sample, ["scan_mode"] = scanMode, ["poll_interval_ms"] = 50, ["interval_s"] = .02, ["timeout_s"] = 2 },
        ["children"] = new JsonArray(new JsonObject { ["type"] = "measurement", ["name"] = "measure_temp" }),
    }.ToJsonString();
    private static (MeasurementRunService Runs, ExperimentImportBindings Bindings) Setup(IRunResultListener? listener = null)
    {
        var port = MockEnvironmentCatalog.PassiveTemperature(); var hub = new NotificationHub(); var registry = new DeviceRegistry([new AxisControllerSession(port, hub)], new(TimeSpan.FromSeconds(5)));
        return (new(registry, hub, listener is null ? null : [listener]), new([], [KeyValuePair.Create("temperature", port.Definition)]));
    }
    [Theory]
    [InlineData("scan_temp_cooling", "linear", null)]
    [InlineData("temperature_cooling", "point", null)]
    [InlineData("temp_cooling", "setpoint", null)]
    [InlineData("scan_temperature", "time", "cooling")]
    [InlineData("scan_temp", "immediate", "temperature_cooling")]
    public async Task ActualScanAliasesAndModesImportSaveExecuteAndRetainActualAxis(string name, string sample, string? mode)
    {
        var (runs, bindings) = Setup(); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter()); var document = workspace.Import(Node(name, sample, mode), bindings); var scan = Assert.IsType<TemperatureCoolingStep>(document.Plan.Root);
        Assert.Equal(sample is "time" or "immediate", document.Preview.HasDynamicLoops); Assert.Equal(Core.Devices.DriverKind.Simulation, Assert.Single(workspace.Devices).DriverKind);
        await runs.StartPlanAsync(document.Plan, Token); Assert.NotEmpty(runs.LatestDataSet!.Rows); Assert.All(runs.LatestDataSet.Rows, row => Assert.Equal(PhysicalUnit.Kelvin, row.Values[scan.Column].Unit)); Assert.True(runs.LatestDataSet.Rows[^1].Values[scan.Column].Value <= scan.EndTemperature.Value + scan.Tolerance.Value); Assert.False(runs.HasActiveRun);
    }
    [Theory]
    [InlineData("sample_mode", "unknown")]
    [InlineData("scan_mode", "ramp")]
    [InlineData("cooling_setpoint_k", "5")]
    [InlineData("end_temp_k", "5")]
    [InlineData("tolerance_k", "-1")]
    [InlineData("timeout_s", "0")]
    [InlineData("poll_interval_ms", "49")]
    [InlineData("interval_s", "0")]
    public void BadCoolingOptionsRejectBeforeIo(string key, string value)
    {
        var node = JsonNode.Parse(Node("scan_temp_cooling"))!.AsObject(); node["params"]![key] = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(value);
        var (runs, bindings) = Setup(); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter()); Assert.ThrowsAny<Exception>(() => workspace.Import(node.ToJsonString(), bindings)); Assert.False(runs.HasActiveRun);
    }
    [Fact]
    public void AscendingAndConflictingTimeoutAliasesAreRejected()
    {
        var (runs, bindings) = Setup(); var importer = new PpmsExperimentPlanImporter(); var node = JsonNode.Parse(Node("scan_temp_cooling"))!.AsObject(); node["params"]!["points"] = new JsonArray(3, 4); Assert.ThrowsAny<Exception>(() => runs.PreviewPlan(importer.Parse(node.ToJsonString(), bindings)));
        node = JsonNode.Parse(Node("scan_temp_cooling"))!.AsObject(); node["params"]!["step_timeout_s"] = 2; Assert.ThrowsAny<Exception>(() => importer.Parse(node.ToJsonString(), bindings));
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("sampling")]
    [InlineData("extra")]
    public void NativeCoolingCannotSilentlyDowngradeUnknownFieldsOrMode(string damage)
    {
        var (runs, bindings) = Setup(); var plan = new PpmsExperimentPlanImporter().Parse(Node("scan_temp_cooling"), bindings); var codec = new JsonExperimentPlanDocument(); var root = JsonNode.Parse(codec.Serialize(plan))!.AsObject(); root["root"]![damage == "extra" ? "ignored" : damage] = "unknown"; if (damage == "kind") Assert.Throws<NotSupportedException>(() => codec.Parse(root.ToJsonString())); else Assert.Throws<FormatException>(() => codec.Parse(root.ToJsonString()));
    }
    [Fact]
    public async Task FreshArchiveCsvAndGraphRetainCoolingBodyAxesAndBlackboard()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cooling-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var (runs, bindings) = Setup(archive); var plan = new PpmsExperimentPlanImporter().Parse(Node("scan_temp_cooling"), bindings); var codec = new JsonExperimentPlanDocument(); plan = codec.Parse(codec.Serialize(plan)); var scan = Assert.IsType<TemperatureCoolingStep>(plan.Root);
            Assert.Contains(codec.DescribeStructure(plan), node => node.Id == scan.Body.Id); await runs.StartPlanAsync(plan, Token); var data = runs.LatestDataSet!; await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.Count, loaded.DataSet.Rows.Count); Assert.Equal(data.Rows[^1].Values, loaded.DataSet.Rows[^1].Values); Assert.NotNull(loaded.DataSet.Blackboard); await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "export.csv"), Token); var text = await File.ReadAllTextAsync(Path.Combine(folder, "export.csv")); Assert.Contains("ppms.root.axis [Kelvin]", text); Assert.Contains("temperature_cooling", text);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public void DeepCoolingCodecAndCompilerPreserve128LevelBoundary(int depth)
    {
        var (runs, bindings) = Setup(); var original = new PpmsExperimentPlanImporter().Parse(Node("scan_temp_cooling"), bindings); var cooling = Assert.IsType<TemperatureCoolingStep>(original.Root); ExperimentStep root = new TemperatureCoolingStep(cooling.Id, cooling.Device, cooling.Column, cooling.Thresholds, cooling.CoolingTarget, cooling.EndTemperature, cooling.Tolerance, cooling.Timeout, cooling.PollInterval, cooling.Sampling, cooling.Interval, new DelayStep(new("body"), TimeSpan.Zero));
        for (var i = 0; i < depth; i++) root = new SequenceStep(new("nested" + i), [root]); var codec = new JsonExperimentPlanDocument(); var plan = codec.Parse(codec.Serialize(new("Deep", root))); Assert.NotNull(runs.PreviewPlan(plan));
    }
}

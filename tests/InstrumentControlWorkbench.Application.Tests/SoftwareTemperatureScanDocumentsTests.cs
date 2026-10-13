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

public sealed class SoftwareTemperatureScanDocumentsTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static JsonObject Node(string name = "scan_temp_ramp", string sampling = "linear", string? mode = null) => new()
    {
        ["type"] = "scan",
        ["name"] = name,
        ["params"] = new JsonObject { ["points"] = new JsonArray(4, 4.01, 4.02), ["ramp_mode"] = "software", ["scan_mode"] = mode, ["rate_k_per_min"] = 20, ["wait_stable_first_point"] = true, ["stable_count"] = 1, ["tolerance_k"] = .0005, ["stable_timeout_s"] = 2, ["timeout_s"] = 2, ["poll_interval_ms"] = 50, ["interval_s"] = .02, ["sample_mode"] = sampling },
        ["children"] = new JsonArray(new JsonObject { ["type"] = "measurement", ["name"] = "measure_temp" }),
    };
    private static (MeasurementRunService Runs, ExperimentImportBindings Bindings) Setup(IRunResultListener? listener = null)
    {
        var port = MockEnvironmentCatalog.PassiveTemperature(); var hub = new NotificationHub(); var registry = new DeviceRegistry([new AxisControllerSession(port, hub)], new(TimeSpan.FromSeconds(5)));
        return (new(registry, hub, listener is null ? null : [listener]), new([], [KeyValuePair.Create("temperature", port.Definition)]));
    }
    [Theory]
    [InlineData("scan_temp_ramp", "linear", null)]
    [InlineData("temperature_ramp", "point", null)]
    [InlineData("temp_ramp", "setpoint", null)]
    [InlineData("scan_temperature", "time", "ramp")]
    [InlineData("scan_temp", "immediate", "temperature_ramp")]
    public async Task ImportedSoftwareAliasesAndSamplingModesExecuteActualKelvin(string name, string sampling, string? mode)
    {
        var (runs, bindings) = Setup(); var workspace = new ExperimentPlanWorkspace(new JsonExperimentPlanDocument(), runs, new PpmsExperimentPlanImporter()); var imported = workspace.Import(Node(name, sampling, mode).ToJsonString(), bindings); var scan = Assert.IsType<SoftwareTemperatureScanStep>(imported.Plan.Root);
        Assert.True(imported.Preview.HasDynamicLoops); await runs.StartPlanAsync(imported.Plan, Token); Assert.NotEmpty(runs.LatestDataSet!.Rows); Assert.All(runs.LatestDataSet.Rows, row => Assert.Equal(PhysicalUnit.Kelvin, row.Values[scan.Column].Unit)); Assert.False(runs.HasActiveRun);
    }
    [Fact]
    public void MissingRampModeImportsAsSoftware()
    {
        var (_, bindings) = Setup(); var node = Node(); node["params"]!.AsObject().Remove("ramp_mode"); Assert.IsType<SoftwareTemperatureScanStep>(new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), bindings).Root);
    }
    [Theory]
    [InlineData("rate_k_per_min", 0)]
    [InlineData("stable_count", 0)]
    [InlineData("stable_timeout_s", 0)]
    [InlineData("timeout_s", 0)]
    [InlineData("tolerance_k", 0)]
    [InlineData("poll_interval_ms", 49)]
    [InlineData("interval_s", 0)]
    public void MalformedControlsRejectBeforeIo(string key, double value)
    {
        var (_, bindings) = Setup(); var node = Node(); node["params"]![key] = value; Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), bindings));
    }
    [Theory]
    [InlineData("ascending-turn")]
    [InlineData("timeout-alias")]
    [InlineData("sample-mode")]
    public void TurningPathConflictsAndUnknownSamplesReject(string damage)
    {
        var (_, bindings) = Setup(); var node = Node(); if (damage == "ascending-turn") node["params"]!["points"] = new JsonArray(4, 5, 4.5); if (damage == "timeout-alias") node["params"]!["step_timeout_s"] = 2; if (damage == "sample-mode") node["params"]!["sample_mode"] = "unknown"; Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), bindings));
    }
    [Fact]
    public async Task NativeFreshArchiveCsvGraphAndBlackboardRetainExecutedSoftwareRamp()
    {
        var folder = Path.Combine(Path.GetTempPath(), "software-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var (runs, bindings) = Setup(archive); var codec = new JsonExperimentPlanDocument(); var plan = codec.Parse(codec.Serialize(new PpmsExperimentPlanImporter().Parse(Node().ToJsonString(), bindings))); var scan = Assert.IsType<SoftwareTemperatureScanStep>(plan.Root);
            Assert.Contains(codec.DescribeStructure(plan), n => n.Id == scan.Body.Id); await runs.StartPlanAsync(plan, Token); var data = runs.LatestDataSet!; await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.Count, loaded.DataSet.Rows.Count); Assert.Equal(data.Rows[^1].Values, loaded.DataSet.Rows[^1].Values); Assert.NotNull(loaded.DataSet.Blackboard); await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "out.csv"), Token); var text = await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")); Assert.Contains("software_temperature_scan", text); Assert.Contains("ppms.root.axis [Kelvin]", text); Assert.Contains("ppms.root.axis.setpoint [Kelvin]", text); Assert.Equal(scan.Points.Values[^1], loaded.DataSet.Rows[^1].Values[scan.SetpointColumn]);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("sampling")]
    [InlineData("extra")]
    [InlineData("rate")]
    public void NativeUnknownFieldsEnumsAndRequiredNullsReject(string damage)
    {
        var (_, bindings) = Setup(); var codec = new JsonExperimentPlanDocument(); var root = JsonNode.Parse(codec.Serialize(new PpmsExperimentPlanImporter().Parse(Node().ToJsonString(), bindings)))!; if (damage == "extra") root["root"]!["ignored"] = true; else root["root"]![damage] = damage == "rate" ? null : "invented"; Assert.Throws<FormatException>(() => codec.Parse(root.ToJsonString()));
    }
    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public void CodecAndCompilerKeepExisting128LevelLimit(int depth)
    {
        var (runs, bindings) = Setup(); var s = Assert.IsType<SoftwareTemperatureScanStep>(new PpmsExperimentPlanImporter().Parse(Node().ToJsonString(), bindings).Root);
        ExperimentStep root = new SoftwareTemperatureScanStep(s.Id, s.Device, s.Column, s.Points, s.Rate, s.WaitStableFirst, s.Tolerance, s.StableCount, s.StableTimeout, s.StepTimeout, s.PollInterval, s.Sampling, s.Interval, new DelayStep(new("body"), TimeSpan.Zero));
        for (var index = 0; index < depth; index++) root = new SequenceStep(new("nested" + index), [root]); var codec = new JsonExperimentPlanDocument(); Assert.NotNull(runs.PreviewPlan(codec.Parse(codec.Serialize(new("Deep", root)))));
    }
}

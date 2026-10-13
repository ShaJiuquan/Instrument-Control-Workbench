using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PolarizationExecutionTests
{
    private static JsonObject PolScan(JsonNode points, JsonArray children, string name = "measure_polarization", string? channel = "none")
    {
        var p = new JsonObject { ["points"] = points }; if (channel is not null) p["channel"] = channel;
        return new() { ["type"] = "scan", ["name"] = name, ["source_line"] = 17, ["params"] = p, ["children"] = children };
    }
    [Theory]
    [InlineData("measure_polarization")]
    [InlineData("pol_voltage")]
    [InlineData("scan_pol_voltage")]
    [InlineData("polarization_sweep")]
    [InlineData("measure_polarization_sweep")]
    public async Task PolarizationIteratorInjectsPointsAndParentChannelWithoutWritingHardwareAxis(string alias)
    {
        var b = new Bench(); var child = PolParameters(); child["pol_voltage_v"] = 20; child["channel"] = "unknown";
        var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(5, 0, -5), new JsonArray(PolNode(child)), alias).ToJsonString(), PolBindings(b));
        var loop = Assert.IsType<ForEachStep>(plan.Root); var pulse = Assert.IsType<ReadPolarizationStep>(Assert.IsType<SequenceStep>(loop.Body).Children[^1]); Assert.IsType<VariableValue>(pulse.PolarizationTarget); Assert.NotNull(pulse.RequestedPolarizationColumn);
        Assert.Equal(17, plan.Metadata["ppms.root.source_line"].Value); await b.Runs.StartPlanAsync(plan, Token); var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(new[] { 5.0, 0, -5 }, b.Port.EnabledAt); Assert.Equal(new[] { 5.0, 0, -5 }, rows.Select(r => r.Values[loop.Variable].Value)); Assert.Equal(new[] { 5.0, 0, -5 }, rows.Select(r => r.Values[pulse.RequestedPolarizationColumn!].Value));
        Assert.Equal(new[] { 1.0, 2, 3 }, rows.Select(r => r.Values[new(loop.Variable + ".index")].Value)); Assert.All(rows, r => Assert.Equal(new(1, PhysicalUnit.Volt), r.Values[pulse.VoltageColumn]));
        Assert.Equal(3, b.Port.Frames); Assert.All(b.Port.Writes.Where(w => w.Enabled), write => Assert.Equal(1, write.Voltage)); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Theory]
    [InlineData("Polarization")]
    [InlineData("polarization_voltage")]
    [InlineData("pol_voltage")]
    [InlineData("scan_pol_voltage")]
    public async Task VoltageScanPolarizationModesDispatchToNonHardwareIterator(string mode)
    {
        var b = new Bench(); var scan = PolScan(new JsonArray(-2, 2), new JsonArray(PolNode(PolParameters())), "scan_voltage"); scan["params"]!["voltage_mode"] = mode;
        var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), PolBindings(b)); Assert.IsType<ForEachStep>(plan.Root); await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(new[] { -2.0, 2 }, b.Port.EnabledAt);
    }
    [Theory]
    [InlineData("{\"start\":-2,\"end\":2,\"points\":3}", "-2,0,2")]
    [InlineData("{\"mode\":\"step\",\"start\":-2,\"end\":2,\"step\":2}", "-2,0,2")]
    [InlineData("{\"mode\":\"log\",\"start\":1,\"end\":100,\"points\":3}", "1,10,100")]
    [InlineData("{\"mode\":\"list\",\"values\":[1,3,2]}", "1,3,2")]
    [InlineData("{\"segments\":[[-2,0],[0,2]]}", "-2,0,2")]
    public void BoundedPointGeneratorsKeepVoltageUnitsAndOrdering(string spec, string expected)
    {
        var b = new Bench(); var plan = new PpmsExperimentPlanImporter().Parse(PolScan(JsonNode.Parse(spec)!, new JsonArray(PolNode(PolParameters()))).ToJsonString(), PolBindings(b));
        var loop = Assert.IsType<ForEachStep>(plan.Root); Assert.Equal(PhysicalUnit.Volt, loop.Values.Unit); var values = expected.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(values.Length, loop.Values.Values.Count); for (var index = 0; index < values.Length; index++) Assert.Equal(values[index], loop.Values.Values[index].Value, 10);
    }
    [Fact]
    public async Task IteratorWithNoMeasurementChildrenPerformsNoInstrumentIo()
    {
        var b = new Bench(); var scan = PolScan(new JsonArray(-5, 5), new JsonArray()); var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), PolBindings(b)); Assert.Empty(plan.Preparations);
        await b.Runs.StartPlanAsync(plan, Token); Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Empty(b.Port.Writes); Assert.Empty(b.Port.EnabledAt); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task InjectionMatchesDirectMeasurementChildrenAndDoesNotRewriteSequenceDescendants()
    {
        var b = new Bench(); var direct = PolParameters(); direct.Remove("pol_voltage_v"); var nested = PolParameters(); nested["pol_voltage_v"] = 7;
        var scan = PolScan(new JsonArray(-2, 2), new JsonArray(PolNode(direct), new JsonObject { ["type"] = "sequence", ["children"] = new JsonArray(PolNode(nested)) }));
        var plan = new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), PolBindings(b)); await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(new[] { -2.0, 7, 2, 7 }, b.Port.EnabledAt); Assert.Equal(4, b.Runs.LatestDataSet!.Rows.Count);
    }
    [Theory]
    [InlineData("voltage")]
    [InlineData("region")]
    [InlineData("unit")]
    public async Task AllLiteralIterationTargetsAreCheckedBeforeConnection(string failure)
    {
        var b = new Bench(); var p = PolParameters(); var values = new JsonArray(5, failure == "voltage" ? 211 : 100); if (failure == "region") p["compliance_a"] = .2;
        var imported = new PpmsExperimentPlanImporter().Parse(PolScan(values, new JsonArray(PolNode(p))).ToJsonString(), PolBindings(b));
        if (failure == "unit")
        {
            var loop = (ForEachStep)imported.Root; imported = b.Plan(new ForEachStep(loop.Id, loop.Variable, new([new(5, PhysicalUnit.Ampere)]), loop.Body));
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => b.Runs.StartPlanAsync(imported, Token)); Assert.Empty(b.Port.Writes); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task PauseRestartsCurrentPointWithoutRepeatingFinishedPointsOrAdvancingAxis()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var p = PolParameters(); p["pol_duration_s"] = .1;
        var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(5, -5), new JsonArray(PolNode(p))).ToJsonString(), PolBindings(b)); var loop = (ForEachStep)plan.Root;
        var running = b.Runs.StartPlanAsync(plan, Token); await b.Port.Enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
        b.Runs.Resume(); await running; Assert.Equal(new[] { 5.0, 5, -5 }, b.Port.EnabledAt); var rows = b.Runs.LatestDataSet!.Rows; Assert.Equal(new[] { 5.0, -5 }, rows.Select(r => r.Values[loop.Variable].Value)); Assert.Equal(new[] { 2.0, 1 }, rows.Select(r => r.Values[new("ppms.root.0.attempts")].Value));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("unit")]
    [InlineData("axis_mismatch")]
    public async Task RequestedPulseVoltageIsMandatoryAndMustAgreeWithRowAxis(string damage)
    {
        var b = new Bench(); var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(5), new JsonArray(PolNode(PolParameters()))).ToJsonString(), PolBindings(b)); await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; var row = Assert.Single(data.Rows); var values = row.Values.ToDictionary(); var column = new ParameterId("ppms.root.0.polarization_voltage");
        if (damage == "missing") values.Remove(column); else values[column] = new(4, damage == "unit" ? PhysicalUnit.Ampere : PhysicalUnit.Volt);
        Assert.ThrowsAny<ArgumentException>(() => new RunDataRowValidator(plan, data.Schema).Validate(new(row.Timestamp, values, environment: row.Environment, quality: row.Quality)));
    }
    [Fact]
    public async Task GuardedDirectChildrenUseVoltageAxisWithoutLosingInjection()
    {
        var b = new Bench(); var child = PolNode(PolParameters()); child["_skipIf"] = new JsonObject { ["variable"] = "pol_voltage", ["op"] = "lt", ["value"] = 0 };
        var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(-2, 2), new JsonArray(child)).ToJsonString(), PolBindings(b)); await b.Runs.StartPlanAsync(plan, Token); Assert.Equal(new[] { 2.0 }, b.Port.EnabledAt); Assert.Single(b.Runs.LatestDataSet!.Rows);
    }
    [Theory]
    [InlineData("{\"points\":[]}")]
    [InlineData("{\"points\":[1,2],\"start\":0}")]
    [InlineData("{\"points\":{\"mode\":\"log\",\"start\":-1,\"end\":2,\"points\":3}}")]
    [InlineData("{\"points\":[1,2],\"instrument\":\"k2450\"}")]
    [InlineData("{\"points\":[1,2],\"voltage_mode\":\"source\"}")]
    public void UnsupportedOrMalformedIteratorControlsRejectWithSourceLine(string text)
    {
        var b = new Bench(); var node = PolScan(new JsonArray(1), new JsonArray(PolNode(PolParameters()))); node["params"] = JsonNode.Parse(text);
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(node.ToJsonString(), PolBindings(b))); Assert.Contains("source line 17", error.Message);
    }
    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public async Task DeepDynamicIteratorAndCodecKeepExistingDepthBoundary(int depth)
    {
        var b = new Bench(); var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(5), new JsonArray(PolNode(PolParameters()))).ToJsonString(), PolBindings(b));
        // Use the leaf directly so deepest pulse stays at depth128.
        var original = (ForEachStep)plan.Root; var pulse = ((SequenceStep)original.Body).Children[^1]; ExperimentStep root = new ForEachStep(new("loop"), original.Variable, original.Values, pulse);
        for (var index = 0; index < depth; index++) root = new SequenceStep(new("nested" + index), [root]);
        var document = new JsonExperimentPlanDocument(); var loaded = document.Parse(document.Serialize(b.Plan(root))); Assert.Equal(depth + 9, b.Runs.PreviewPlan(loaded).MaximumStepExecutions); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public async Task AbortAfterCompletedPointRetainsItWithoutProducingLaterPoints()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); var p = PolParameters(); p["pol_duration_s"] = .1;
        var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(5, 0, -5), new JsonArray(PolNode(p))).ToJsonString(), PolBindings(b)); var running = b.Runs.StartPlanAsync(plan, Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Abort(); await running; Assert.Single(b.Runs.LatestDataSet!.Rows); Assert.Equal(1, b.Port.Frames); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task NativeSaveFreshArchiveCsvAndReplayKeepRequestedPointsAndOriginalFrames()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pol-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive); var plan = new PpmsExperimentPlanImporter().Parse(PolScan(new JsonArray(5, 0, -5), new JsonArray(PolNode(PolParameters()))).ToJsonString(), PolBindings(b));
            var document = new JsonExperimentPlanDocument(); await document.SaveAsync(Path.Combine(folder, "plan.json"), plan, Token); plan = await document.LoadAsync(Path.Combine(folder, "plan.json"), Token); await b.Runs.StartPlanAsync(plan, Token);
            var data = b.Runs.LatestDataSet!; await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.SelectMany(r => r.Quality), loaded.DataSet.Rows.SelectMany(r => r.Quality));
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "data.csv"), Token); Assert.Contains("ppms.root.0.polarization_voltage [Volt]", await File.ReadAllTextAsync(Path.Combine(folder, "data.csv")));
            var replay = new Bench(); await replay.Runs.StartPlanAsync(loaded.DataSet.Plan!, Token); Assert.Equal(new[] { 5.0, 0, -5 }, replay.Port.EnabledAt);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

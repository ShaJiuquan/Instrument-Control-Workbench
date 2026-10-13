using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class PolarizationExecutionTests
{
    private static JsonObject PolNode(JsonObject p, string name = "measure_polarization_readout") => new() { ["type"] = "measurement", ["name"] = name, ["source_line"] = 23, ["params"] = p };
    private static JsonObject PolParameters() => new() { ["channel"] = "none", ["pol_voltage_v"] = 5, ["read_voltage_v"] = 1, ["pol_duration_s"] = 0, ["read_delay_s"] = 0 };
    private static ExperimentImportBindings PolBindings(Bench b) => new(new Dictionary<string, InstrumentId> { ["k2450"] = b.Port.Id }, [], b.Plan(b.Pulse()).Preparations);
    [Theory]
    [InlineData("measure_polarization_readout")]
    [InlineData("pol_readout")]
    [InlineData("polarization_readout")]
    [InlineData("polread")]
    public async Task ImportAliasesPreserveNativePulseAndSourceLine(string name)
    {
        var b = new Bench(); var p = PolParameters(); p["inter_pulse_duration_s"] = .001; p["meter_range_a"] = .001;
        var plan = new PpmsExperimentPlanImporter().Parse(PolNode(p, name).ToJsonString(), PolBindings(b));
        var pulse = Assert.IsType<ReadPolarizationStep>(plan.Root); Assert.Equal(new(.001, PhysicalUnit.Ampere), pulse.Sense.ManualRange); Assert.Equal(23, plan.Metadata["ppms.root.source_line"].Value);
        var doc = new JsonExperimentPlanDocument(); var loaded = doc.Parse(doc.Serialize(plan)); await b.Runs.StartPlanAsync(loaded, Token);
        Assert.Equal(new(1, PhysicalUnit.Dimensionless), Assert.Single(b.Runs.LatestDataSet!.Rows).Values[pulse.AttemptsColumn]); Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled);
    }
    [Theory]
    [InlineData("pol_voltage_v", "211")]
    [InlineData("pol_duration_s", "-1")]
    [InlineData("compliance_a", "0")]
    [InlineData("nplc", "11")]
    [InlineData("meter_range_a", "0")]
    [InlineData("sensing_mode", "\"4wire\"")]
    [InlineData("channel", "\"unknown\"")]
    [InlineData("maximum_attempts", "101")]
    [InlineData("maximum_attempts", "1.5")]
    [InlineData("pol_voltage", "5")]
    [InlineData("filter_enabled", "true")]
    public async Task MalformedPulseImportRejectsBeforeConnectionWithSourceLocation(string field, string value)
    {
        var b = new Bench(); var p = PolParameters(); p[field] = JsonNode.Parse(value);
        var error = Assert.ThrowsAny<Exception>(() => new PpmsExperimentPlanImporter().Parse(PolNode(p).ToJsonString(), PolBindings(b))); Assert.Contains("source line 23", error.Message); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
    [Fact]
    public void PulseImportRequiresExplicitChannelAndCannotInheritSourceScans()
    {
        var b = new Bench(); var p = PolParameters(); p.Remove("channel"); Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(PolNode(p).ToJsonString(), PolBindings(b)));
        var scan = new JsonObject { ["type"] = "scan", ["name"] = "scan_voltage", ["params"] = new JsonObject { ["start"] = 0, ["end"] = 1, ["points"] = 2, ["channel"] = "none" }, ["children"] = new JsonArray(PolNode(PolParameters())) };
        var error = Assert.Throws<FormatException>(() => new PpmsExperimentPlanImporter().Parse(scan.ToJsonString(), PolBindings(b))); Assert.Contains("outside enclosing source scans", error.Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenMatrixRouteIsVerifiedBeforeAndAfterPulseAndRunAddedConnectionsCleaned(bool drift)
    {
        var b = new Bench(); var matrix = new MockMatrixSwitch(new(new(new("matrix"), "K3706", "Matrix simulator", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 6)], [b.Port.Id]));
        var point = new MatrixCrossPoint(1, 1, 1); var route = new RouteMatrixStep(new("route"), matrix.Definition.Device.Id, [point], TimeSpan.Zero);
        var pulse = b.Pulse(); pulse = new(pulse.Id, pulse.Device, pulse.PolarizationVoltage, pulse.PolarizationDuration, pulse.InterPulseVoltage, pulse.InterPulseDuration, pulse.ReadVoltage, pulse.ReadDelay, pulse.Compliance, pulse.Sense, pulse.VoltageColumn, pulse.CurrentColumn, pulse.AttemptsColumn, requiredRoute: new(matrix.Definition.Device.Id, [point]));
        var registry = new DeviceRegistry([new InstrumentManager(b.Port, b.Hub), new MatrixSwitchSession(matrix, b.Hub)], b.Leases); var runs = new MeasurementRunService(registry, b.Hub);
        if (drift) b.Port.AfterFrame = () => matrix.CloseAsync([new(1, 2, 2)], Token);
        var plan = b.Plan(new SequenceStep(new("root"), [route, pulse])); var doc = new JsonExperimentPlanDocument(); plan = doc.Parse(doc.Serialize(plan));
        if (drift) { await Assert.ThrowsAsync<InvalidOperationException>(() => runs.StartPlanAsync(plan, Token)); Assert.Empty(runs.LatestDataSet!.Rows); }
        else { await runs.StartPlanAsync(plan, Token); Assert.Single(runs.LatestDataSet!.Rows); }
        Assert.False((await b.Port.GetSnapshotAsync(Token)).OutputEnabled); Assert.DoesNotContain(point, (await matrix.GetSnapshotAsync(Token)).Closed); Assert.False(runs.HasActiveRun);
    }
    [Theory]
    [InlineData("quality")]
    [InlineData("attempts")]
    [InlineData("fraction")]
    [InlineData("different_time")]
    public async Task ArchiveProvenanceRejectsMissingOrForgedAttemptAndFrameEvidence(string damage)
    {
        var b = new Bench(); var pulse = b.Pulse(); var plan = b.Plan(pulse); await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!; var row = Assert.Single(data.Rows); var values = row.Values.ToDictionary(); var quality = row.Quality.ToArray();
        if (damage == "quality") quality = [];
        if (damage == "attempts") values.Remove(pulse.AttemptsColumn);
        if (damage == "fraction") values[pulse.AttemptsColumn] = new(1.5, PhysicalUnit.Dimensionless);
        if (damage == "different_time") quality[0] = new(pulse.Id, pulse.Device, pulse.VoltageColumn, quality[0].CapturedAt.AddSeconds(-1), null);
        Assert.Throws<ArgumentException>(() => new RunDataRowValidator(plan, data.Schema).Validate(new(row.Timestamp, values, quality: quality)));
    }
    [Fact]
    public async Task NativeSaveFreshArchiveCsvAndCalculatedRowsPreserveActualPulseInputs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "polarization-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); var b = new Bench(archive); var pulse = b.Pulse();
            var calc = new CalculatedMeasurementStep(new("calculate"), pulse, [new(new("resistance"), new ArithmeticValue(ArithmeticOperator.Divide, new VariableValue(pulse.VoltageColumn), new VariableValue(pulse.CurrentColumn)))]);
            var doc = new JsonExperimentPlanDocument(); await doc.SaveAsync(Path.Combine(folder, "plan.json"), b.Plan(calc), Token); await b.Runs.StartPlanAsync(await doc.LoadAsync(Path.Combine(folder, "plan.json"), Token), Token);
            var data = b.Runs.LatestDataSet!; Assert.Equal(2, data.Rows.Count); Assert.Equal(PhysicalUnit.Ohm, data.Rows[1].Values[new("resistance")].Unit); Assert.Equal(data.Rows[0].Quality, data.Rows[1].Quality);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(data.Id, Token); Assert.Equal(data.Rows.SelectMany(r => r.Quality), loaded.DataSet.Rows.SelectMany(r => r.Quality));
            Assert.IsType<ReadPolarizationStep>(Assert.IsType<CalculatedMeasurementStep>(loaded.DataSet.Plan!.Root).Body);
            await fresh.ExportCsvAsync(data.Id, Path.Combine(folder, "out.csv"), Token); Assert.Contains("attempts [Dimensionless]", await File.ReadAllTextAsync(Path.Combine(folder, "out.csv")));
            var replay = new Bench(); await replay.Runs.StartPlanAsync(loaded.DataSet.Plan, Token); Assert.Equal(data.Rows[1].Values[new("resistance")], replay.Runs.LatestDataSet!.Rows[1].Values[new("resistance")]);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public async Task MaximumDepthPulseCompilationRemainsMetadataOnly(int depth)
    {
        var b = new Bench(); ExperimentStep root = b.Pulse(); for (var index = 0; index < depth; index++) root = new SequenceStep(new("nested" + index), [root]);
        Assert.Equal(depth + 8, b.Runs.PreviewPlan(b.Plan(root)).MaximumStepExecutions); Assert.False((await b.Port.GetSnapshotAsync(Token)).Connected);
    }
}

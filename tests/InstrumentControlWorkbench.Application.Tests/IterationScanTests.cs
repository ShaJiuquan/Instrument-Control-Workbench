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
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class IterationScanTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [Theory]
    [InlineData("{\"start\":3,\"end\":0,\"step\":1}", "3,2,1,0")]
    [InlineData("{\"points_mode\":\"list\",\"values\":[0,2,2,5]}", "0,2,2,5")]
    [InlineData("{\"points\":[2.0000000001,0]}", "2,0")]
    [InlineData("{\"points\":{\"mode\":\"linear\",\"start\":0,\"end\":4,\"points\":3}}", "0,2,4")]
    [InlineData("{\"points_mode\":\"segments\",\"segments\":[[0,1],[9,2]]}", "0,1,2")]
    [InlineData("{\"points\":{\"mode\":\"segments\",\"segments\":[[0,1],[1,2]]}}", "0,1,2")]
    public async Task NumberScanRetainsOrderDuplicatesAndIntegerAxesWithoutDeviceWrites(string points, string expected)
    {
        var b = new Bench();
        var plan = b.Import(Node("scan", "scan_number", JsonNode.Parse(points)!.AsObject(), FieldRead()));
        Assert.Empty(plan.Preparations); Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
        await b.Runs.StartPlanAsync(plan, Token);
        var rows = b.Runs.LatestDataSet!.Rows;
        Assert.Equal(expected, string.Join(",", rows.Select(r => r.Values[new("ppms.root.axis")].Value)));
        Assert.Equal(Enumerable.Range(1, rows.Count).Select(n => (double)n), rows.Select(r => r.Values[new("ppms.root.axis.index")].Value));
        Assert.All(rows, row => { Assert.Equal(rows.Count, row.Values[new("ppms.root.axis.total")].Value); b.Runs.LatestDataSet.Schema.ValidateRow(row); });
        Assert.Single(b.Runs.LatestDataSet.Instruments);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
        Assert.False(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("scan_number", "{\"points\":[-1,0]}")]
    [InlineData("scan_number", "{\"points\":[0,0.1]}")]
    [InlineData("scan_number", "{\"start\":0,\"end\":1,\"points\":3}")]
    [InlineData("scan_number", "{\"axis\":\"scan_time\",\"points\":[1]}")]
    [InlineData("scan_number", "{\"points\":[1],\"start\":99}")]
    [InlineData("scan_number", "{\"points\":[]}")]
    [InlineData("scan_number", "{\"points_mode\":\"segments\",\"segments\":[[1]],\"step\":1}")]
    [InlineData("scan_time", "{\"points\":[-0.01]}")]
    [InlineData("scan_time", "{\"points\":[4294967.295]}")]
    [InlineData("scan_time", "{\"points\":[1],\"rate\":1}")]
    [InlineData("scan_time", "{\"points\":[null]}")]
    [InlineData("scan_time", "{\"points_mode\":\"foo\",\"start\":0,\"end\":1,\"points\":2}")]
    [InlineData("scan_number", "{\"points\":{\"mode\":\"foo\",\"segments\":[[0,1]]}}")]
    public async Task InvalidIterationReportsSourceLineAndDoesNotConnect(string name, string points)
    {
        var b = new Bench(); var node = Node("scan", name, JsonNode.Parse(points)!.AsObject(), FieldRead()); node["source_line"] = 17;
        var error = Assert.Throws<FormatException>(() => b.Import(node)); Assert.Contains("source line 17", error.Message);
        Assert.Null(b.Runs.LatestDataSet); Assert.False(b.Runs.HasActiveRun);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public void LegacyNativeIteratorDoesNotAcquireTimingAndUnknownTimingCannotBeDowngraded()
    {
        var doc = new JsonExperimentPlanDocument();
        var original = new ExperimentPlan("legacy", new ForEachStep(new("loop"), new("value"), new([new(-0.1, PhysicalUnit.Second)]), new DelayStep(new("body"), TimeSpan.Zero)));
        var json = doc.Serialize(original); Assert.DoesNotContain("timing", json);
        Assert.Equal(IterationTiming.Immediate, Assert.IsType<ForEachStep>(doc.Parse(json).Root).Timing);
        var node = JsonNode.Parse(json)!.AsObject(); node["root"]!["timing"] = "42";
        Assert.Throws<FormatException>(() => doc.Parse(node.ToJsonString()));
        node["root"]!["timing"] = "DelayBeforePoint";
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Parse(node.ToJsonString()));
    }

    [Fact]
    public async Task TimeAliasWaitsEachPointBeforeMeasurementAndKeepsRequestedDelaySeparateFromElapsedTime()
    {
        var b = new Bench(); var plan = b.Import(Node("scan", "time_s", new() { ["axis"] = "time_s", ["points"] = new JsonArray(0.04, 0.06) }, FieldRead()));
        Assert.Equal(IterationTiming.DelayBeforePoint, Assert.IsType<ForEachStep>(plan.Root).Timing);
        await b.Runs.StartPlanAsync(plan, Token); var data = b.Runs.LatestDataSet!;
        Assert.Equal(new[] { 0.04, 0.06 }, data.Rows.Select(r => r.Values[new("ppms.root.axis")].Value));
        Assert.True(data.Rows[0].Values[new("elapsed")].Value >= 0.039);
        Assert.True(data.Rows[1].Values[new("elapsed")].Value >= 0.099);
        Assert.Equal(PhysicalUnit.Second, data.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("ppms.root.axis")).Parameter.Unit);
        Assert.Equal("Delay before point", data.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("ppms.root.axis")).Parameter.Label);
    }

    private static JsonObject FieldRead() => Node("measurement", "measure_field");
    private static JsonObject Node(string type, string name, JsonObject? p = null, params JsonObject[] children)
        => new() { ["type"] = type, ["name"] = name, ["params"] = p?.DeepClone() ?? new JsonObject(), ["children"] = new JsonArray(children.Select(c => c.DeepClone()).ToArray()) };
    private static async Task Until(NotificationSubscription events, WorkbenchNotificationKind kind, StepId? step = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { var fact = await events.Reader.ReadAsync(deadline.Token); if (fact.Kind == kind && (step is null || fact.Step == step)) return; }
    }
    private sealed class Bench
    {
        public NotificationHub Hub { get; } = new();
        public InstrumentLeaseService Leases { get; } = new(TimeSpan.FromSeconds(5));
        public MockKeithley2450SourceMeter Source { get; } = new();
        public IAxisControllerPort Field { get; }
        public DeviceRegistry Registry { get; }
        public ExperimentImportBindings Bindings { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public MeasurementRunService Runs { get; }
        public Bench(IRunResultListener? archive = null, IAxisControllerPort? field = null)
        {
            Field = field ?? MockEnvironmentCatalog.Field();
            var manager = new InstrumentManager(Source, Hub);
            Registry = new([manager, new AxisControllerSession(Field, Hub)], Leases);
            Runs = new(manager, Leases, Hub, resultListeners: archive is null ? null : [archive], deviceRegistry: Registry);
            Workspace = new(new JsonExperimentPlanDocument(), Runs, new PpmsExperimentPlanImporter());
            Bindings = new([KeyValuePair.Create("k2450", Source.Id)], [KeyValuePair.Create("field", Field.Definition)]);
        }
        public ExperimentPlan Import(JsonObject node) => Workspace.Import(node.ToJsonString(), Bindings).Plan;
        public ReadStep ReadSource(string id) => new(new(id), [new(new(id + ".voltage"), new(Source.Id, Keithley2450Parameters.MeasuredVoltage.Id))]);
        public ExperimentPlan Powered(ExperimentStep body) => new("powered timed loop", new SequenceStep(new("root"),
            [new SourceOutputStep(new("on"), Source.Id, true), body]),
            [new(Source.Id, SourceFunction.Voltage, new(0.01, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.TwoWire, 1, true))]);
    }
}

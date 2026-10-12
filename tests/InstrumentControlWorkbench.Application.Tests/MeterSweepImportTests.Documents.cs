using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class MeterSweepImportTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("missing_device")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("fractional")]
    [InlineData("oversized")]
    [InlineData("null_source")]
    public void MalformedNativeReadRouteCannotBeSilentlyIgnored(string damage)
    {
        var codec = new JsonExperimentPlanDocument(); var text = codec.Serialize(new("Guarded read", new ReadStep(new("read"),
            [new(new("value"), new(new("meter"), new("voltage")))], requiredRoute: new(new("matrix"), [new(1, 1, 1)]))));
        var json = JsonNode.Parse(text)!.AsObject(); var read = json["root"]!.AsObject(); var route = read["requiredRoute"]!.AsObject();
        switch (damage)
        {
            case "null_source": route["source"] = null; break;
            case "null": read["requiredRoute"] = null; break;
            case "unknown": route["settle"] = 0; break;
            case "missing_device": route.Remove("device"); break;
            case "empty": route["closed"] = new JsonArray(); break;
            case "duplicate": route["closed"]!.AsArray().Add(route["closed"]![0]!.DeepClone()); break;
            case "fractional": route["closed"]![0]!["row"] = 1.5; break;
            case "oversized": route["closed"] = new JsonArray(Enumerable.Range(1, 4097).Select(i => (JsonNode)new JsonObject { ["slot"] = 1, ["row"] = 1, ["column"] = i }).ToArray()); break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(json.ToJsonString()));
    }
    [Fact]
    public void SharedLargeReadRoutesAreBudgetedBeforeNativeDomAllocation()
    {
        var route = new MatrixRouteExpectation(new("matrix"), Enumerable.Range(1, 4096).Select(i => new Core.Devices.MatrixCrossPoint(1, 1, i)));
        var plan = new ExperimentPlan("Route size budget", new SequenceStep(new("root"), Enumerable.Range(0, 80).Select(i =>
            new ReadStep(new("read." + i), [new(new("value." + i), new(new("meter"), new("voltage")))], requiredRoute: route))));
        Assert.Throws<FormatException>(() => new JsonExperimentPlanDocument().Serialize(plan));
    }
    [Fact]
    public async Task PublishedViFixtureUsesActualProfilesAndSafeSimulatedRuntime()
    {
        var root = AppContext.BaseDirectory; var catalog = await new JsonDeviceConfigurationDocument().LoadAsync(Path.Combine(root, "devices", "workbench-devices.json"), Token);
        var hub = new NotificationHub(); var runtime = new ConfiguredDeviceRuntime(catalog, new ConfiguredDeviceFactory(), new(TimeSpan.FromSeconds(5)), hub);
        var profiles = await new PpmsRoutingProfileImporter().LoadAsync(Path.Combine(root, "profiles", "channel_profiles.json"), Path.Combine(root, "profiles", "samples.json"), Path.Combine(root, "profiles", "instrument_ports.json"), Token);
        var source = new Core.Instruments.InstrumentId("smu-primary");
        var bindings = profiles.Bind(runtime.CreateImportBindings([new(source, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(0.001, PhysicalUnit.Ampere), new(SenseFunction.Current, SensingMode.FourWire, 1, true))]),
            new("matrix-1"), [KeyValuePair.Create(new WiringProfileId("k2450"), source)], new("k2450"));
        var plan = await new PpmsExperimentPlanImporter().LoadAsync(Path.Combine(root, "plans", "ppms-vi-meter-import.json"), bindings, Token);
        var runs = new MeasurementRunService(runtime.Registry, hub); await runs.StartPlanAsync(plan, Token); Assert.Equal(3, runs.LatestDataSet!.Rows.Count);
        Assert.Equal(new[] { -0.1, 0, 0.1 }, runs.LatestDataSet.Rows.Select(r => r.Values[new("ppms.root.axis")].Value));
        Assert.True((await runtime.Registry.GetStatusAsync(source, Token)).SafetyConfirmed); Assert.True((await runtime.Registry.GetStatusAsync(new("matrix-1"), Token)).SafetyConfirmed);
        Assert.True((await runtime.Registry.GetStatusAsync(new("smu-secondary"), Token)).SafetyConfirmed); Assert.False(runs.HasActiveRun);
    }
    [Fact]
    public async Task RoutedViMeterCanParticipateInWideMeasurementGroup()
    {
        await using var peer = new Keithley2450ScpiPeer(); var b = new Bench(peer); var tree = Scan(SensingMode.TwoWire);
        var second = tree["children"]![0]!.DeepClone(); second["label"] = "Second input observation"; tree["children"]!.AsArray().Add(second);
        var codec = new JsonExperimentPlanDocument(); var plan = new PpmsExperimentPlanImporter().Parse(tree.ToJsonString(), b.Bindings);
        await b.Runs.StartPlanAsync(codec.Parse(codec.Serialize(plan)), Token); Assert.Equal(9, b.Runs.LatestDataSet!.Rows.Count);
        Assert.Equal(3, b.Runs.LatestDataSet.Rows.Count(row => row.MeasurementGroup is not null));
        Assert.All(b.Runs.LatestDataSet.Rows.Where(row => row.MeasurementGroup is not null), row => { Assert.NotNull(row.MeasurementGroup); Assert.Equal(2, row.Quality.Count); Assert.Equal(2, row.MeasurementGroup.Inputs.Count); });
        Assert.Equal(1, b.Matrix.Closes); Assert.False(peer.Output); Assert.False(b.Runs.HasActiveRun);
    }
}

using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PlanSampleDetailsEditingTests
{
    [Fact]
    public async Task DetailsUpdateUsesCanonicalExistingSampleAndRetainsAllRouteAndEnvelopeAuthority()
    {
        var b = new Bench(); var original = b.Plan.Routing!.Samples.Single(); var before = b.Codec.Serialize(b.Plan);
        var details = new SampleProfileDetails("Edited Hall", "original-2", "GaAs", "Si", "first line\nsecond line", new(new(.004, PhysicalUnit.Meter), new(.008, PhysicalUnit.Meter), new(.000002, PhysicalUnit.Meter), new(.001, PhysicalUnit.Meter)));
        var updated = b.Workspace.UpdateSampleDetails(b.Plan, new(original.Id.Value.ToUpperInvariant()), details); var sample = Assert.Single(updated.Plan.Routing!.Samples);
        Assert.Equal(original.Id, sample.Id); Assert.Equal(original.Topology, sample.Topology); Assert.Equal(original.Electrodes, sample.Electrodes); Assert.Equal(details.Geometry, sample.Geometry); Assert.Equal(details.Name, sample.Name); Assert.Equal(details.OriginalId, sample.OriginalId); Assert.Equal(details.Material, sample.Material); Assert.Equal(details.Substrate, sample.Substrate); Assert.Equal(details.Notes, sample.Notes);
        Assert.Same(b.Plan.Root, updated.Plan.Root); Assert.Equal(b.Plan.Preparations, updated.Plan.Preparations); Assert.Equal(b.Plan.CurrentSourcePreparations, updated.Plan.CurrentSourcePreparations); Assert.Equal(b.Plan.Metadata, updated.Plan.Metadata); Assert.Equal(b.Plan.MaximumStepExecutions, updated.Plan.MaximumStepExecutions); Assert.Equal(b.Plan.Routing.Channels, updated.Plan.Routing.Channels); Assert.Equal(b.Plan.Routing.Wirings, updated.Plan.Routing.Wirings); Assert.Equal(b.Plan.Routing.Matrix, updated.Plan.Routing.Matrix);
        Assert.Equal(before, b.Codec.Serialize(b.Plan)); var parsed = b.Codec.Parse(updated.Text); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(updated.Text), JsonNode.Parse(b.Codec.Serialize(parsed)))); await b.AssertNoIO();
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("noCatalog")]
    [InlineData("blankName")]
    [InlineData("longName")]
    [InlineData("longMaterial")]
    [InlineData("nullGeometry")]
    [InlineData("invalidOriginalPlan")]
    public async Task InvalidDetailsDoNotMutatePlanOrConnectInstruments(string damage)
    {
        var b = new Bench(); var before = b.Codec.Serialize(b.Plan); var original = b.Plan.Routing!.Samples.Single(); var details = new SampleProfileDetails(original.Name, original.OriginalId, original.Material, original.Substrate, original.Notes, original.Geometry);
        details = damage switch { "blankName" => details with { Name = "" }, "longName" => details with { Name = new string('x', 1025) }, "longMaterial" => details with { Material = new string('x', 8193) }, "nullGeometry" => details with { Geometry = null! }, _ => details };
        var plan = damage == "noCatalog" ? new ExperimentPlan("plain", new DelayStep(new("delay"), TimeSpan.Zero)) : damage == "invalidOriginalPlan" ? new ExperimentPlan("missing preparation", b.Plan.Root, routing: b.Plan.Routing) : b.Plan;
        if (damage == "noCatalog") Assert.Throws<InvalidOperationException>(() => b.Workspace.UpdateSampleDetails(plan, original.Id, details));
        else Assert.ThrowsAny<ArgumentException>(() => b.Workspace.UpdateSampleDetails(plan, new(damage == "unknown" ? "missing" : original.Id.Value), details));
        Assert.Equal(before, b.Codec.Serialize(b.Plan)); await b.AssertNoIO();
    }
    private sealed class Bench
    {
        public MockKeithley2450SourceMeter Source { get; } = new();
        public MockMatrixSwitch Matrix { get; }
        public JsonExperimentPlanDocument Codec { get; } = new();
        public ExperimentPlan Plan { get; }
        public ExperimentPlanWorkspace Workspace { get; }
        public Bench()
        {
            Plan = Codec.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plans/sample-hall-analysis.plan.json")));
            var hub = new NotificationHub(); var manager = new InstrumentManager(Source, hub); Matrix = new(new(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 12)], [Source.Id]));
            var runs = new MeasurementRunService(new DeviceRegistry([manager, new MatrixSwitchSession(Matrix, hub)], new(TimeSpan.FromSeconds(5))), hub); Workspace = new(Codec, runs);
        }
        public async Task AssertNoIO() { Assert.False((await Source.GetSnapshotAsync(CancellationToken.None)).Connected); Assert.False((await Matrix.GetSnapshotAsync(CancellationToken.None)).Connected); }
    }
}

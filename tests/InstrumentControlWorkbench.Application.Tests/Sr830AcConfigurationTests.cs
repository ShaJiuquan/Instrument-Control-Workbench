using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Instruments.Ports;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class Sr830AcConfigurationTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly InstrumentId Generator = new("ac-generator");
    private static readonly InstrumentId Isolator = new("ac-isolator");
    private static DeviceConfigurationCatalog Catalog(string model = "SR830-AC-SIM", bool enabled = true,
        InstrumentId? generator = null, MatrixCrossPoint? point = null)
    {
        var cap = new MatrixAcIsolationCapability(generator ?? Generator, [point ?? new(1, 1, 1)], new(5, PhysicalUnit.Volt), new(102000, PhysicalUnit.Hertz));
        return new(null,
        [new(Generator, model, "AC simulation", ConfiguredDeviceCategory.LockIn, new SimulationDeviceConnection(), enabled),
         new(Isolator, "K3706", "Qualified simulated isolation", ConfiguredDeviceCategory.Matrix, new SimulationDeviceConnection(), matrix: new([new(1, 2, 2)], [], cap))], []);
    }
    [Fact]
    public async Task ExplicitActiveProfileAndQualificationRoundTripWithoutConnecting()
    {
        var codec = new JsonDeviceConfigurationDocument(); var catalog = codec.Parse(codec.Serialize(Catalog()));
        var cap = catalog.Devices.Single(d => d.Id == Isolator).Matrix!.AcIsolation!;
        Assert.Equal(Generator, cap.Generator); Assert.Equal(new Quantity(5, PhysicalUnit.Volt), cap.MaximumAmplitude);
        Assert.Equal(new Quantity(102000, PhysicalUnit.Hertz), cap.MaximumFrequency);
        var factory = new ConfiguredDeviceFactory(); var generator = factory.Create(catalog.Devices.Single(d => d.Id == Generator)).LockIn!;
        Assert.IsAssignableFrom<ISr830ExcitationPort>(generator); Assert.False((await generator.GetSnapshotAsync(Token)).Connected);
        var matrix = factory.Create(catalog.Devices.Single(d => d.Id == Isolator)).Matrix!;
        Assert.Equal(cap, matrix.Definition.AcIsolation); Assert.False((await matrix.GetSnapshotAsync(Token)).Connected);
        var passive = factory.Create(new(Generator, "SR830", "Passive", ConfiguredDeviceCategory.LockIn, new SimulationDeviceConnection())).LockIn!;
        Assert.False(passive is ISr830ExcitationPort);
    }
    [Fact]
    public void OneGeneratorCannotHaveTwoIndependentlyOperatedIsolationMatrices()
    {
        var first = Catalog(); var matrix = first.Devices.Single(d => d.Id == Isolator);
        var second = new DeviceConfiguration(new("another-isolator"), matrix.Model, "Second", matrix.Category, matrix.Connection, matrix: matrix.Matrix);
        Assert.Throws<ArgumentException>(() => new DeviceConfigurationCatalog(null, [.. first.Devices, second], []));
    }
    [Theory]
    [InlineData("passive")]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("outside-card")]
    [InlineData("self")]
    public void InvalidQualificationCannotReachStartup(string damage)
    {
        Assert.ThrowsAny<ArgumentException>(() => Catalog(damage == "passive" ? "SR830" : "SR830-AC-SIM", damage != "disabled",
            damage == "missing" ? new("absent") : damage == "self" ? Isolator : null,
            damage == "outside-card" ? new(1, 3, 1) : null));
    }
    [Theory]
    [InlineData("unknown-cap-field")]
    [InlineData("unknown-path-field")]
    [InlineData("wrong-envelope-unit")]
    [InlineData("narrow-envelope")]
    [InlineData("partial-cap")]
    public void StrictDeviceDocumentRejectsDamagedQualification(string damage)
    {
        var codec = new JsonDeviceConfigurationDocument(); var node = JsonNode.Parse(codec.Serialize(Catalog()))!;
        var cap = node["devices"]![1]!["matrix"]!["acIsolation"]!;
        switch (damage)
        {
            case "unknown-cap-field": cap["safe"] = true; break;
            case "unknown-path-field": cap["connections"]![0]!["safe"] = true; break;
            case "wrong-envelope-unit": cap["maxAmplitude"]!["unit"] = "Ampere"; break;
            case "narrow-envelope": cap["maxAmplitude"]!["value"] = .004; break;
            default: cap.AsObject().Remove("generator"); break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(node.ToJsonString()));
    }
    [Theory]
    [InlineData("unknown-permission")]
    [InlineData("duplicate-path")]
    [InlineData("wrong-amplitude-unit")]
    [InlineData("zero-amplitude")]
    [InlineData("bad-input")]
    [InlineData("negative-settle")]
    public async Task NativeActiveRecipeRejectsMalformedFields(string damage)
    {
        var codec = new JsonExperimentPlanDocument();
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plans", "headless-sr830-ac.plan.json"));
        var node = JsonNode.Parse(text)!; var step = node["root"]!["body"]!;
        switch (damage)
        {
            case "unknown-permission": step["safe"] = true; break;
            case "duplicate-path": step["connections"]!.AsArray().Add(step["connections"]![0]!.DeepClone()); break;
            case "wrong-amplitude-unit": step["sineAmplitude"]!["unit"] = "Ampere"; break;
            case "zero-amplitude": step["sineAmplitude"]!["value"] = 0; break;
            case "bad-input": step["input"] = "Voltage"; break;
            default: step["settleSeconds"] = -.01; break;
        }
        Assert.ThrowsAny<Exception>(() => codec.Parse(node.ToJsonString()));
    }
    [Fact]
    public void NativeActiveNodeIsAtomicAndPreservesTimerTicks()
    {
        var step = new ReadSr830AcFrameStep(new("ac"), Generator, Isolator, [new(1, 1, 1)],
            new(new(13.7, PhysicalUnit.Hertz), new(.004, PhysicalUnit.Volt), 2), Sr830InputMode.Current1M, new("raw"), TimeSpan.FromTicks(1234567));
        var codec = new JsonExperimentPlanDocument(); var restored = codec.Parse(codec.Serialize(new ExperimentPlan("active", step)));
        var active = Assert.IsType<ReadSr830AcFrameStep>(restored.Root);
        Assert.Equal(step.Settle, active.Settle); Assert.Equal(step.Id, active.Id); Assert.Equal(step.Excitation, active.Excitation);
        Assert.Equal(step.Connections, active.Connections); Assert.Single(codec.DescribeStructure(restored));
    }
    [Fact]
    public async Task SavedSimulationFixtureRunsAndFreshArchiveRetainsIsolationEvidenceAndCsv()
    {
        var folder = Path.Combine(Path.GetTempPath(), "active-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var devicePath = Path.Combine(AppContext.BaseDirectory, "devices", "headless-sr830-ac.devices.json");
            var plan = await new JsonExperimentPlanDocument().LoadAsync(Path.Combine(AppContext.BaseDirectory, "plans", "headless-sr830-ac.plan.json"), Token);
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs"));
            var started = await new DeviceConfigurationStartup(new JsonDeviceConfigurationDocument(), new ConfiguredDeviceFactory(), new InstrumentLeaseService(TimeSpan.FromSeconds(5)), new()).StartAsync(devicePath, null, Token, [archive]);
            await started.Runs.StartPlanAsync(plan, Token); var data = started.Runs.LatestDataSet!;
            Assert.Equal(3, data.Rows.Count); Assert.All(data.Rows, row => Assert.Single(row.Sr830Ac)); Assert.False(started.Runs.HasActiveRun);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var reloaded = await fresh.LoadAsync(data.Id, Token);
            Assert.Equal(3, reloaded.DataSet.Rows.Count);
            foreach (var pair in data.Rows.Zip(reloaded.DataSet.Rows))
            {
                var original = Assert.Single(pair.First.Sr830Ac); var restored = Assert.Single(pair.Second.Sr830Ac);
                Assert.Equal(original.Step, restored.Step); Assert.Equal(original.Reference, restored.Reference); Assert.Equal(original.Connections, restored.Connections);
                Assert.Equal(original.BeforeConfigurationOpen.Closed, restored.BeforeConfigurationOpen.Closed);
                Assert.Equal(original.AfterAcquisitionOpen.Closed, restored.AfterAcquisitionOpen.Closed);
            }
            var csvPath = Path.Combine(folder, "data.csv"); await fresh.ExportCsvAsync(data.Id, csvPath, Token);
            var csv = await File.ReadAllTextAsync(csvPath); Assert.Contains("sr830_ac_json", csv); Assert.Contains("beforeConfigurationOpen", csv); Assert.Contains("afterAcquisitionOpen", csv);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

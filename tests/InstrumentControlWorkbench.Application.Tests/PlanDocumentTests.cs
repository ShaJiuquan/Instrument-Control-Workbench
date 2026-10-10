using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class PlanDocumentTests
{
    private readonly JsonExperimentPlanDocument documents = new();

    [Fact]
    public async Task EverySupportedStepRoundTripsAndLoadedPlanRunsOnFreshSessions()
    {
        var (plan, runs, source) = Create();
        var encoded = documents.Serialize(plan);
        var decoded = documents.Parse(encoded);
        Assert.Equal(encoded, documents.Serialize(decoded));
        Assert.Equal(PhysicalUnit.Volt, decoded.Metadata["bias"].Unit);
        var workspace = new ExperimentPlanWorkspace(documents, runs);
        var inspected = workspace.Validate(encoded);
        Assert.Equal(2, inspected.Preview.Devices.Count);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        await runs.StartPlanAsync(decoded, CancellationToken.None);
        Assert.Equal(7, runs.LatestDataSet!.Rows.Count);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
        Assert.False(runs.HasActiveRun);
    }

    [Fact]
    public async Task RepositoryExampleLoadsAndRunsFromDiskWithFreshDevices()
    {
        var (_, runs, source) = Create();
        var path = Path.Combine(AppContext.BaseDirectory, "plans", "field-voltage.plan.json");
        var document = await new ExperimentPlanWorkspace(documents, runs).LoadAsync(path, CancellationToken.None);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
        await runs.StartPlanAsync(document.Plan, CancellationToken.None);
        Assert.Equal(15, runs.LatestDataSet!.Rows.Count);
        Assert.Equal(2, runs.LatestDataSet.Instruments.Count);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }

    [Fact]
    public async Task SaveLoadAndAtomicReplacementPreservePreviousFileWhenCancelledOrInvalid()
    {
        var (plan, runs, _) = Create();
        var directory = Path.Combine(Path.GetTempPath(), $"icw-plans-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "sample.plan.json");
        try
        {
            var workspace = new ExperimentPlanWorkspace(documents, runs);
            var original = documents.Serialize(plan);
            await workspace.SaveAsync(path, original, CancellationToken.None);
            Assert.Equal(original, (await workspace.LoadAsync(path, CancellationToken.None)).Text);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => documents.SaveAsync(path,
                new("replacement", plan.Root, plan.Preparations, plan.Metadata), cancelled.Token));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            var invalid = JsonNode.Parse(original)!;
            invalid["root"] = new JsonObject { ["kind"] = "output", ["id"] = "unknown", ["device"] = "missing", ["enabled"] = true };
            await Assert.ThrowsAsync<KeyNotFoundException>(() => workspace.SaveAsync(path, invalid.ToJsonString(), CancellationToken.None));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            await documents.SaveAsync(path, new("replacement", plan.Root, plan.Preparations, plan.Metadata), CancellationToken.None);
            Assert.Equal("replacement", (await documents.LoadAsync(path, CancellationToken.None)).Name);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("future_version")]
    [InlineData("unknown_field")]
    [InlineData("unknown_kind")]
    [InlineData("wrong_unit")]
    [InlineData("numeric_enum")]
    [InlineData("missing_name")]
    [InlineData("missing_root")]
    [InlineData("negative_delay")]
    [InlineData("huge_delay")]
    [InlineData("duplicate_field")]
    public async Task CorruptUnsupportedOrAmbiguousDocumentsFailBeforeHardware(string corruption)
    {
        var (plan, runs, source) = Create();
        var root = JsonNode.Parse(documents.Serialize(plan))!;
        switch (corruption)
        {
            case "future_version": root["version"] = 999; break;
            case "unknown_field": root["unsafeFallback"] = true; break;
            case "unknown_kind": root["root"]!["kind"] = "parallel"; break;
            case "wrong_unit": root["preparations"]![0]!["level"]!["unit"] = "Tesla"; break;
            case "numeric_enum": root["preparations"]![0]!["function"] = "1"; break;
            case "missing_name": root.AsObject().Remove("name"); break;
            case "missing_root": root.AsObject().Remove("root"); break;
            case "negative_delay": root["root"] = new JsonObject { ["kind"] = "delay", ["id"] = "bad", ["seconds"] = -1 }; break;
            case "huge_delay": root["root"] = new JsonObject { ["kind"] = "delay", ["id"] = "bad", ["seconds"] = 1e12 }; break;
        }
        var text = root.ToJsonString();
        if (corruption == "duplicate_field") text = text.Replace("\"version\":1", "\"version\":1,\"version\":999", StringComparison.Ordinal);
        Assert.NotNull(Record.Exception(() => new ExperimentPlanWorkspace(documents, runs).Validate(text)));
        Assert.False(runs.HasActiveRun);
        Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).Connected);
    }

    [Fact]
    public void ExcessiveDocumentAndTreeDepthAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => documents.Parse(new string(' ', JsonExperimentPlanDocument.MaximumBytes + 1)));
        ExperimentStep step = new DelayStep(new("last"), TimeSpan.Zero);
        for (var i = 0; i < 130; i++) step = new RepeatStep(new($"r{i}"), 1, step);
        Assert.Throws<FormatException>(() => documents.Serialize(new("deep", step)));
        var values = new ScanValues(Enumerable.Repeat(new Quantity(0, PhysicalUnit.Volt), 1_000_000));
        var shared = new SequenceStep(new("root"), Enumerable.Range(0, 3).Select(i =>
            new ForEachStep(new($"loop{i}"), new($"iterator{i}"), values, new DelayStep(new($"delay{i}"), TimeSpan.Zero))));
        Assert.Throws<FormatException>(() => documents.Serialize(new("shared", shared)));
    }

    private static (ExperimentPlan, MeasurementRunService, MockKeithley2450SourceMeter) Create()
    {
        var hub = new NotificationHub();
        var source = new MockKeithley2450SourceMeter();
        var manager = new InstrumentManager(source, hub);
        var field = MockEnvironmentCatalog.Field();
        var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var registry = new DeviceRegistry([manager, new AxisControllerSession(field, hub)], leases);
        var runs = new MeasurementRunService(manager, leases, hub, deviceRegistry: registry);
        var address = new ParameterAddress(source.Id, Keithley2450Parameters.SourceVoltage.Id);
        var readback = new ParameterAddress(field.Definition.Device.Id, field.Definition.Readback.Id);
        var v = new ParameterId("bias");
        var iterator = new ParameterId("iterator");
        var plan = new ExperimentPlan("portable", new SequenceStep(new("root"),
        [
            new SourceOutputStep(new("on"), source.Id, true),
            new AssignValueStep(new("assign"), v, new ConstantValue(new(0.1, PhysicalUnit.Volt))),
            new SetParameterStep(new("set"), address, new VariableValue(v)),
            new DelayStep(new("delay"), TimeSpan.Zero),
            new RepeatStep(new("repeat"), 2, new ForEachStep(new("iterator"), iterator,
                ScanValues.Linear(new(0.1, PhysicalUnit.Volt), new(0.2, PhysicalUnit.Volt), 2),
                new SequenceStep(new("body"), [new SetParameterStep(new("iter_set"), address, new VariableValue(iterator)),
                    new ReadStep(new("iter_read"), [new(new("iter_current"), new(source.Id, Keithley2450Parameters.MeasuredCurrent.Id))])]))),
            new ScanStep(new("scan"), new(field.Definition.Device.Id, field.Definition.Target.Id), new("field_axis"),
                ScanValues.Linear(new(0, PhysicalUnit.Tesla), new(0.1, PhysicalUnit.Tesla), 2), TimeSpan.Zero,
                new ReadStep(new("field_read"), [new(new("field"), readback)])),
            new IfElseStep(new("condition"), new(v, ComparisonOperator.Greater, new ConstantValue(new(0, PhysicalUnit.Volt))),
                new ReadStep(new("yes"), [new(new("final_current"), new(source.Id, Keithley2450Parameters.MeasuredCurrent.Id))]),
                new ReadStep(new("no"), [new(new("final_voltage"), address)])),
            new WaitStableStep(new("stable"), readback, new ConstantValue(new(0, PhysicalUnit.Tesla)), new(9, PhysicalUnit.Tesla),
                TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1)),
            new SourceOutputStep(new("off"), source.Id, false),
        ]), [new(source.Id, SourceFunction.Voltage, new(0, PhysicalUnit.Volt), new(105e-6, PhysicalUnit.Ampere),
            new(SenseFunction.Current, SensingMode.FourWire, 1, true))], new Dictionary<string, Quantity> { ["bias"] = new(0.1, PhysicalUnit.Volt) });
        return (plan, runs, source);
    }
}

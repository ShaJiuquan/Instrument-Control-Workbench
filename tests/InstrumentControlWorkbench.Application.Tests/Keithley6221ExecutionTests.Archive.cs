using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Fact]
    public async Task PureSourceWidePointPreservesModelEvidenceAfterFreshArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "k6221-wide-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var writer = new FileRunArchive(folder); await using var b = new Bench(writer); var first = b.Read;
            var second = new ReadCurrentResistanceStep(new("second"), first.Source, first.Meter, first.CurrentReference, new("second.V"), new("second.I"), new("second.R"), first.MinimumCurrent, first.ExpectedProgrammedCurrent, b.Route);
            var grouped = new MeasurementGroupStep(new("point"), [new("direct", first), new("routed", second)]);
            var plan = new ExperimentPlan("Pure wide", new SequenceStep(new("root"), [new RouteMatrixStep(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero), new SourceOutputStep(new("on"), first.Source, true), grouped]), currentSourcePreparations: [b.Preparation]);
            await b.Runs.StartPlanAsync(plan, Token); var row = b.Runs.LatestDataSet!.Rows[^1]; Assert.NotNull(row.MeasurementGroup); Assert.Equal(2, row.IndependentResistance.Count);
            await using var fresh = new FileRunArchive(folder); var loaded = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token);
            Assert.Equal(row.IndependentResistance, loaded.DataSet.Rows[^1].IndependentResistance); Assert.All(loaded.DataSet.Rows[^1].IndependentResistance, e => Assert.Equal(CurrentSourceModel.Keithley6221, e.SourceConfiguration.Model));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("missingModel")]
    [InlineData("wrongModel")]
    [InlineData("unknownModel")]
    [InlineData("badRange")]
    [InlineData("compliance")]
    [InlineData("measuredReference")]
    public async Task DamagedModelProvenanceCannotBeLoadedOrReplaceCsv(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "k6221-damaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var b = new Bench(); var native = b.Plan(false); var plan = new ExperimentPlan(native.Name, native.Root,
                currentSourcePreparations: [new(b.Source.Descriptor.Id, new(new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt)))]);
            await b.Runs.StartPlanAsync(plan, Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(Path.Combine(folder, "runs")))
            { await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token); await writer.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token); }
            var path = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson"); var row = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var e = row["independentResistance"]![0]!; var source = e["sourceConfiguration"]!.AsObject();
            switch (damage)
            {
                case "missingModel": source.Remove("model"); break;
                case "wrongModel": source["model"] = "Keithley2450"; break;
                case "unknownModel": source["model"] = "Other"; break;
                case "badRange": source["manualRange"] = new JsonObject { ["value"] = 12e-6, ["unit"] = "Ampere" }; break;
                case "compliance": source["compliance"] = 0.09; break;
                case "measuredReference": e["currentReference"] = "MeasuredSourceReadback"; break;
            }
            await File.WriteAllTextAsync(path, row.ToJsonString() + "\n"); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token)); var csv = Path.Combine(folder, "previous.csv"); await File.WriteAllTextAsync(csv, "accepted previous data");
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("accepted previous data", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task LegacyK2450IndependentArchiveWithoutModelTagStillLoads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "legacy-current-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new MockKeithley2450SourceMeter(new("smu"), "K2450 simulation"); var meter = new MockKeithley2182Meter(new("meter"), "Passive simulation"); var hub = new NotificationHub();
            var registry = new DeviceRegistry([new InstrumentManager(source, hub), new Keithley2182MeterSession(meter, hub)], new(TimeSpan.FromSeconds(5))); var runs = new MeasurementRunService(registry, hub);
            var read = new ReadCurrentResistanceStep(new("read"), source.Id, meter.Descriptor.Id, CurrentReference.ProgrammedSetpoint, new("V"), new("I"), new("R"), new(0, PhysicalUnit.Ampere));
            await runs.StartPlanAsync(new("Legacy model", new SequenceStep(new("root"), [new SourceOutputStep(new("on"), source.Id, true), read]),
                [new(source.Id, SourceFunction.Current, new(1e-5, PhysicalUnit.Ampere), new(10, PhysicalUnit.Volt), new(SenseFunction.Voltage, SensingMode.TwoWire, 1, true))]), Token);
            var snapshot = runs.LatestDataSet!.Capture(); await using (var writer = new FileRunArchive(folder))
            { await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token); await writer.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token); }
            var path = Path.Combine(folder, snapshot.Id.Value.ToString("N"), "rows.ndjson"); var row = JsonNode.Parse(await File.ReadAllTextAsync(path))!; row["independentResistance"]![0]!["sourceConfiguration"]!.AsObject().Remove("model");
            await File.WriteAllTextAsync(path, row.ToJsonString() + "\n"); await using var fresh = new FileRunArchive(folder); var loaded = await fresh.LoadAsync(snapshot.Id, Token);
            Assert.Equal(CurrentSourceModel.Keithley2450, Assert.Single(Assert.Single(loaded.DataSet.Rows).IndependentResistance).SourceConfiguration.Model);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task CurrentWriteAfterConfirmedOffInvalidatesOldStopProof()
    {
        await using var b = new Bench(); b.Source.OnConfigured = settings => { if (settings.Current.Value == 2e-5) b.Source.RefuseStop = true; return Task.CompletedTask; };
        var plan = new ExperimentPlan("Fresh stop", new SequenceStep(new("root"), [new SourceOutputStep(new("off"), b.Source.Descriptor.Id, false),
            new SetParameterStep(new("change"), new(b.Source.Descriptor.Id, Keithley6221Parameters.Current.Id), new ConstantValue(new(2e-5, PhysicalUnit.Ampere)))]), currentSourcePreparations: [b.Preparation]);
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(plan, Token)); Assert.True(b.Runs.HasActiveRun); Assert.True(b.Leases.IsLeased(b.Source.Descriptor.Id, DateTimeOffset.UtcNow.AddYears(1)));
        b.Source.RefuseStop = false; b.Source.OnConfigured = null; await b.Runs.RecoverSafetyAsync(Token); Assert.False(b.Runs.HasActiveRun);
    }
}

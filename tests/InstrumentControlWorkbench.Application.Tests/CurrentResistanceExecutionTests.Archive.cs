using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class CurrentResistanceExecutionTests
{
    [Fact]
    public async Task WidePointRetainsBothIndependentReferencesWithValueEqualRoutesAfterReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "current-wide-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(Path.Combine(folder, "runs")); await using var b = new Bench(archive); b.Peer.ActualSource = 8e-6;
            var group = new MeasurementGroupStep(new("point"), [new("programmed", b.Read(id: "first", route: b.Route)), new("measured", b.Read(CurrentReference.MeasuredSourceReadback, "second", b.Route))]);
            var plan = new ExperimentPlan("Wide current resistance", new SequenceStep(new("root"),
                [new RouteMatrixStep(new("route"), b.Route.Device, b.Route.Closed, TimeSpan.Zero), new SourceOutputStep(new("on"), b.Source.Id, true), group]), [b.Preparation]);
            await b.Runs.StartPlanAsync(plan, Token);
            var point = b.Runs.LatestDataSet!.Rows[^1]; Assert.NotNull(point.MeasurementGroup); Assert.Equal(2, point.IndependentResistance.Count);
            Assert.Equal(100, point.IndependentResistance[0].Resistance.Value, 8); Assert.Equal(125, point.IndependentResistance[1].Resistance.Value, 8); Assert.Equal(4, point.Quality.Count);
            await using var fresh = new FileRunArchive(Path.Combine(folder, "runs")); var loaded = await fresh.LoadAsync(b.Runs.LatestDataSet.Id, Token);
            Assert.Equal(point.IndependentResistance, loaded.DataSet.Rows[^1].IndependentResistance);
            await fresh.ExportCsvAsync(b.Runs.LatestDataSet.Id, Path.Combine(folder, "wide.csv"), Token);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("current")]
    [InlineData("resistance")]
    [InlineData("source")]
    [InlineData("route")]
    [InlineData("reference")]
    [InlineData("numericReference")]
    [InlineData("threshold")]
    [InlineData("time")]
    [InlineData("sourceLevel")]
    public async Task DamagedIndependentEvidenceCannotReplaceExistingCsvEvenWithoutCompletionChecksum(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "current-damaged-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var b = new Bench(); await b.Runs.StartPlanAsync(b.Plan(b.Read(route: b.Route)), Token); var snapshot = b.Runs.LatestDataSet!.Capture();
            await using (var writer = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                await writer.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
                await writer.OnRowAsync(snapshot.Id, snapshot.Rows[0], Token);
            }
            var path = Path.Combine(folder, "runs", snapshot.Id.Value.ToString("N"), "rows.ndjson"); var row = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); var e = row["independentResistance"]![0]!;
            switch (damage)
            {
                case "missing": row.Remove("independentResistance"); break;
                case "current": e["current"]!["value"] = 2e-5; break;
                case "resistance": row["values"]!["R"]!["value"] = 99; break;
                case "source": e["source"] = "meter"; break;
                case "route": e["route"]!["closed"]![0]!["column"] = 3; break;
                case "reference": e["currentReference"] = "MeasuredSourceReadback"; break;
                case "numericReference": e["currentReference"] = "0"; break;
                case "threshold": e["minimumCurrent"]!["value"] = 0; break;
                case "time": e["voltageCapturedAt"] = snapshot.StartedAt.AddDays(-1); break;
                case "sourceLevel": e["sourceConfiguration"]!["level"] = 2e-5; break;
            }
            await File.WriteAllTextAsync(path, row.ToJsonString() + "\n"); await using var fresh = new FileRunArchive(Path.Combine(folder, "runs"));
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.LoadAsync(snapshot.Id, Token));
            var csv = Path.Combine(folder, "existing.csv"); await File.WriteAllTextAsync(csv, "previous accepted export");
            await Assert.ThrowsAnyAsync<Exception>(() => fresh.ExportCsvAsync(snapshot.Id, csv, Token)); Assert.Equal("previous accepted export", await File.ReadAllTextAsync(csv));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

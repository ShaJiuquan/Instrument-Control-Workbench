using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    private static async Task<(RunId Id, string Final)> SeedBlackboardArchiveAsync(string folder)
    {
        await using var archive = new FileRunArchive(folder); var b = new Bench(archive);
        await b.Runs.StartPlanAsync(new("Archive seed", new SequenceStep(new("root"), [TypedWrite("flag_seed", "flag", new BooleanValue(true)),
            TypedWrite("label_seed", "label", new TextValue("晶圆,\"Si\"\n")), Local("number_seed", "number", new(2, PhysicalUnit.Volt))])), Token);
        var id = b.Runs.LatestDataSet!.Id;
        return (id, Path.Combine(folder, id.Value.ToString("N"), "final.json"));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("nonwriter")]
    [InlineData("variable")]
    [InlineData("kind")]
    [InlineData("extra_payload")]
    [InlineData("negative_elapsed")]
    [InlineData("future_elapsed")]
    [InlineData("duplicate")]
    [InlineData("null_entry")]
    [InlineData("wrong_quantity_unit")]
    [InlineData("literal_changed")]
    [InlineData("oversize_text")]
    [InlineData("missing_status")]
    [InlineData("unknown_status")]
    [InlineData("remove_payload")]
    [InlineData("remove_pair_and_status")]
    [InlineData("missing_hash")]
    [InlineData("checksum")]
    [InlineData("version_mismatch")]
    public async Task BlackboardArchiveRejectsCorruptSnapshotsAndPreservesExistingExport(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-corrupt-" + Guid.NewGuid().ToString("N"));
        var export = folder + ".csv";
        try
        {
            var seeded = await SeedBlackboardArchiveAsync(folder); var final = JsonNode.Parse(await File.ReadAllTextAsync(seeded.Final))!.AsObject();
            var snapshot = final["blackboard"]!.AsObject(); Assert.Equal(final["blackboardSha256"]!.GetValue<string>(), SnapshotHash(snapshot));
            var entries = snapshot["entries"]!.AsArray(); var flag = entries.Single(e => e!["variable"]!.GetValue<string>() == "flag")!;
            switch (damage)
            {
                case "owner": snapshot["id"] = Guid.NewGuid().ToString("D"); break;
                case "source": flag["source"] = "missing"; break;
                case "nonwriter": flag["source"] = "root"; break;
                case "variable": flag["variable"] = "undeclared"; break;
                case "kind": flag["kind"] = "SubtreeOutput"; break;
                case "extra_payload": flag["value"]!["text"] = "true"; break;
                case "negative_elapsed": flag["elapsedTicks"] = -1; break;
                case "future_elapsed": flag["elapsedTicks"] = snapshot["elapsedTicks"]!.GetValue<long>() + 1; break;
                case "duplicate": entries.Add(flag.DeepClone()); break;
                case "null_entry": entries[0] = null; break;
                case "wrong_quantity_unit": entries.Single(e => e!["variable"]!.GetValue<string>() == "number")!["value"]!["quantity"]!["unit"] = "Ampere"; break;
                case "literal_changed": flag["value"]!["boolean"] = false; break;
                case "oversize_text": entries.Single(e => e!["variable"]!.GetValue<string>() == "label")!["value"]!["text"] = new string('x', BlackboardText.MaximumLength + 1); break;
                case "missing_status": final.Remove("blackboardStatus"); break;
                case "unknown_status": final["blackboardStatus"] = "unknown"; break;
                case "remove_payload": final.Remove("blackboard"); break;
                case "remove_pair_and_status": final.Remove("blackboard"); final.Remove("blackboardSha256"); final.Remove("blackboardStatus"); break;
                case "missing_hash": final.Remove("blackboardSha256"); break;
                case "checksum": flag["value"]!["boolean"] = false; break;
                case "version_mismatch": final["version"] = 1; break;
            }
            if (damage is not ("checksum" or "remove_pair_and_status" or "missing_hash") && final["blackboard"] is { } payload) final["blackboardSha256"] = SnapshotHash(payload);
            await File.WriteAllTextAsync(seeded.Final, final.ToJsonString(BlackboardJson)); await File.WriteAllTextAsync(export, "keep existing data");
            await using var reopened = new FileRunArchive(folder);
            Assert.NotNull(await Record.ExceptionAsync(() => reopened.ReadMetadataAsync(seeded.Id, Token)));
            Assert.NotNull(await Record.ExceptionAsync(() => reopened.LoadAsync(seeded.Id, Token)));
            Assert.Single((await reopened.ListAsync(Token)).Issues);
            Assert.NotNull(await Record.ExceptionAsync(() => reopened.ExportCsvAsync(seeded.Id, export, Token)));
            Assert.Equal("keep existing data", await File.ReadAllTextAsync(export)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(export)!, Path.GetFileName(export) + ".*.tmp"));
        }
        finally { File.Delete(export); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task BlackboardArchiveReadsLegacyVersionOneAsUnknownAndObservedEmptyAsDistinct()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seeded = await SeedBlackboardArchiveAsync(folder);
            var headerPath = Path.Combine(folder, seeded.Id.Value.ToString("N"), "metadata.json");
            var header = JsonNode.Parse(await File.ReadAllTextAsync(headerPath))!; header["version"] = 1; await File.WriteAllTextAsync(headerPath, header.ToJsonString(BlackboardJson));
            var final = JsonNode.Parse(await File.ReadAllTextAsync(seeded.Final))!.AsObject(); final["version"] = 1; final.Remove("blackboard"); final.Remove("blackboardSha256"); final.Remove("blackboardStatus");
            await File.WriteAllTextAsync(seeded.Final, final.ToJsonString(BlackboardJson));
            await using var reopened = new FileRunArchive(folder); var legacy = await reopened.LoadAsync(seeded.Id, Token);
            Assert.True(legacy.IsFinalized); Assert.Null(legacy.DataSet.Blackboard); Assert.Empty(legacy.DataSet.Rows);
            var b = new Bench(reopened); await b.Runs.StartPlanAsync(new("Observed empty", new DelayStep(new("wait"), TimeSpan.Zero)), Token);
            var actual = await reopened.LoadAsync(b.Runs.LatestDataSet!.Id, Token); Assert.NotNull(actual.DataSet.Blackboard); Assert.Empty(actual.DataSet.Blackboard.Entries);
            Assert.Equal(actual.DataSet.Id, actual.DataSet.Blackboard.Id);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task BlackboardArchiveRefusesTerminalSnapshotAtStartOrForeignOwnershipAtFinalization()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-writer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seeded = await SeedBlackboardArchiveAsync(folder); await using var archive = new FileRunArchive(folder);
            var original = (await archive.LoadAsync(seeded.Id, Token)).DataSet; var id = RunId.New();
            var changed = original with { Id = id };
            await Assert.ThrowsAsync<ArgumentException>(() => archive.OnStartedAsync(changed with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token));
            Assert.False(Directory.Exists(Path.Combine(folder, id.Value.ToString("N"))));
            await archive.OnStartedAsync(changed with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [], Blackboard = null }, Token);
            await Assert.ThrowsAsync<ArgumentException>(() => archive.OnFinalizedAsync(changed, Token));
            Assert.False((await archive.ReadMetadataAsync(id, Token)).IsFinalized); Assert.False(File.Exists(Path.Combine(folder, id.Value.ToString("N"), "final.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task BlackboardArchiveOversizedRuntimePayloadFaultsDeliveryAfterSafeStopWithoutTruncation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blackboard-large-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(folder); var b = new Bench(archive);
            var writes = Enumerable.Range(0, 150).Select(i => TypedWrite("copy" + i, "copy" + i, new VariableValue(new("seed"))));
            var plan = b.Powered(new SequenceStep(new("body"), [TypedWrite("seed", "seed", new TextValue(new string('x', BlackboardText.MaximumLength))), .. writes, b.ReadSource("observed")]));
            var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)); Assert.NotNull(error); Assert.Contains("8 MiB", error.Message);
            Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestOutcome!.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            Assert.Equal(152, b.Runs.LatestBlackboard!.Count); Assert.Single(b.Runs.LatestDataSet!.Rows);
            var partial = await archive.LoadAsync(b.Runs.LatestDataSet.Id, Token); Assert.False(partial.IsFinalized); Assert.Null(partial.DataSet.Blackboard); Assert.Single(partial.DataSet.Rows);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

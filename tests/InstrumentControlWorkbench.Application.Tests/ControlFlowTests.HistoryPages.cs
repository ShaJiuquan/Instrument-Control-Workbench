using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightweightCatalogDistinguishesObservedEmptyAndFullVariableHistoryWithoutLosingSelectedMetadata(bool typed)
    {
        var folder = Path.Combine(Path.GetTempPath(), "history-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id; RunBlackboardSnapshot expected;
            var metadata = new RunMetadata(new("wafer"), "硅片 Si", new string('测', 60_000), new Dictionary<string, string> { ["operator"] = new string('x', 8192) });
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(archive); ExperimentStep body = typed ? new SequenceStep(new("root"), [TypedWrite("label", "sample", new TextValue(new string('硅', 50_000))), TypedWrite("flag", "ready", new BooleanValue(true))]) : new DelayStep(new("empty"), TimeSpan.Zero);
                await b.Runs.StartPlanAsync(new("Wafer history", body), Token, metadata); id = b.Runs.LatestOutcome!.Id; expected = b.Runs.LatestBlackboardSnapshot!;
                Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected);
            }
            await using var history = new RunHistoryService(new FileRunArchive(folder));
            var summary = Assert.Single((await history.BrowseAsync(new(sampleId: new("wafer")), Token)).Runs);
            Assert.Equal(id, summary.Id); Assert.Equal("硅片 Si", summary.SampleName); Assert.True(summary.HasPlan); Assert.Equal(0, summary.InstrumentCount); Assert.Equal(typed ? 2 : 0, summary.BlackboardEntryCount);
            var detailed = await history.ReadMetadataAsync(id, Token); Assert.Equal(metadata.Notes, detailed.Header.Metadata!.Notes); Assert.Equal(metadata.Attributes["operator"], detailed.Header.Metadata.Attributes["operator"]);
            var page = await history.ReadPageAsync(id, new(), Token); Assert.Empty(page.Rows); Assert.Empty(page.Metadata.Header.Rows); Assert.Equal(0, page.ObservedRowCount); Assert.Equal(0, page.ReturnedDataBytes);
            Assert.Equal(expected.CapturedAt, page.Metadata.Header.Blackboard!.CapturedAt); Assert.Equal(expected.Entries.Count, page.Metadata.Header.Blackboard.Entries.Count);
            foreach (var entry in expected.Entries) Assert.Equal(entry.Value, page.Metadata.Header.Blackboard.Entries[entry.Key]);
            Assert.False(page.HasMore); Assert.NotNull(page.Version.FinalSha256);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

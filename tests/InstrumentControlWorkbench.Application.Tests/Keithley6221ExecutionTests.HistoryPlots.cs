using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class Keithley6221ExecutionTests
{
    [Fact]
    public async Task HistoryPlotKeepsRecoveredAcquisitionFailureAsAGapWithFailedQuality()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"icw-failed-plot-{Guid.NewGuid():N}");
        try
        {
            await using var archive = new FileRunArchive(folder); await using var b = new Bench(archive);
            b.Meter.OnRead = _ => throw new InstrumentReadException(b.Meter.Descriptor.Id, InstrumentReadFailureKind.Overrange, "No measurement");
            await b.Runs.StartPlanAsync(RecoveryPlan(b), Token);
            await using var fresh = new FileRunArchive(folder);
            var plot = await new RunHistoryService(fresh).ReadPlotPageAsync(b.Runs.LatestDataSet!.Id, new(), new("elapsed"), b.Read.ResistanceColumn, Token);
            var sample = Assert.Single(plot.Samples);
            Assert.Equal(1, sample.RowIndex); Assert.NotNull(sample.X); Assert.Null(sample.Y); Assert.False(sample.HasCoordinates);
            Assert.Equal(ReadingQualityState.ContainsFailedAcquisitions, sample.QualityState);
            Assert.Equal(ReadingQualityIssues.None, sample.QualityIssues); Assert.Equal(0, sample.ObservedQualityColumns);
            Assert.Equal(HistoryPlotRowKind.Reading, sample.Kind); Assert.True(plot.Metadata.IsFinalized);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

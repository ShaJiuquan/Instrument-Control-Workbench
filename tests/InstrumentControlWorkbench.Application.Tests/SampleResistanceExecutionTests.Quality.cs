using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class SampleResistanceExecutionTests
{
    [Theory]
    [InlineData(SampleTopology.HallBar)]
    [InlineData(SampleTopology.VanDerPauw)]
    public async Task PhysicalBufferWarningsPropagateThroughAnalysisAndWidePointWithoutInventingNewFrames(SampleTopology topology)
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(folder.Path);
        await using var peer = new Keithley2450ScpiPeer { ExtraSourceStatus = 32, MeasurementStatus = 1 };
        var source = new TcpKeithley2450SourceMeter(new("k2450"), "TCP source", peer.Server.Configuration()); var hub = new NotificationHub();
        var matrix = new MockMatrixSwitch(new MatrixSwitchDefinition(new(new("matrix"), "K3706", "Matrix", DriverKind.Simulation, DeviceStopPolicy.OpenRunAddedConnections, []), [new(1, 4, 6)], [source.Id]));
        var manager = new InstrumentManager(source, hub); var leases = new InstrumentLeaseService(TimeSpan.FromSeconds(5));
        var runs = new MeasurementRunService(manager, leases, hub, [archive], deviceRegistry: new([manager, new MatrixSwitchSession(matrix, hub)], leases));
        var sample = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(topology, Geometry)));
        var point = new MeasurementGroupStep(new("point"), [new("sample", sample.Root), new("pair", new SequenceStep(new("pair_root"),
            [new SourceOutputStep(new("pair_on"), source.Id, true), new ReadSourceSenseStep(new("pair_read"), source.Id, SourceFunction.Voltage, new("pair_V"), new("pair_I"))]))]);
        await runs.StartPlanAsync(new("quality point", point, sample.Preparations, routing: sample.Routing), Token);
        var rows = runs.LatestDataSet!.Rows; var analyzed = Assert.Single(rows, r => r.Analysis is not null); var wide = rows[^1];
        var issue = ReadingQualityIssues.SourceLimited | ReadingQualityIssues.Questionable;
        Assert.Equal(analyzed.Analysis!.InputCount * 3, analyzed.ObservedQualityColumns); Assert.Equal(issue, analyzed.QualityIssues);
        Assert.Equal(rows.Take(analyzed.Analysis.InputCount).SelectMany(r => r.Quality).ToArray(), analyzed.Quality.ToArray());
        Assert.Equal(analyzed.Quality.Concat(rows[^2].Quality).ToArray(), wide.Quality.ToArray()); Assert.Equal(issue, wide.QualityIssues);
        Assert.Contains(wide.Quality, q => q.CapturedAt > wide.Timestamp); // Separate sequential acquisitions remain separate timestamps.
        await using var fresh = new FileRunArchive(folder.Path); var saved = await fresh.LoadAsync(runs.LatestDataSet.Id, Token);
        Assert.Equal(wide.Quality.ToArray(), saved.DataSet.Rows[^1].Quality.ToArray()); Assert.Equal(issue, saved.DataSet.Rows[^1].QualityIssues);
        Assert.False(peer.Output); Assert.Empty((await matrix.GetSnapshotAsync(Token)).Closed); Assert.False(runs.HasActiveRun); await source.DisconnectAsync(Token);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAnalysisWithNoEvidenceStaysUnobservedButMixedLegacyInputsCannotClaimCompleteQuality(bool mixed)
    {
        using var folder = new TemporaryFolder(); var b = new Bench(); await b.Runs.StartAnalyzedSampleResistanceAsync(Recipe(Configuration(SampleTopology.HallBar, Geometry)), Token);
        var snapshot = b.Runs.LatestDataSet!.Capture(); await using var archive = new FileRunArchive(folder.Path);
        await archive.OnStartedAsync(snapshot with { State = InstrumentControlWorkbench.Core.Measurements.MeasurementRunState.Running, FinishedAt = null, Rows = [] }, Token);
        for (var i = 0; i < snapshot.Rows.Count; i++)
        {
            var row = snapshot.Rows[i];
            var replacement = !mixed || i == 0 ? new MeasurementDataRow(row.Timestamp, row.Values, row.Routing, row.Analysis, row.MeasurementGroup, row.Environment) : row;
            if (mixed && row.Analysis is not null) await Assert.ThrowsAsync<ArgumentException>(() => archive.OnRowAsync(snapshot.Id, replacement, Token));
            else await archive.OnRowAsync(snapshot.Id, replacement, Token);
        }
        if (!mixed)
        {
            await using var fresh = new FileRunArchive(folder.Path); var saved = await fresh.LoadAsync(snapshot.Id, Token);
            Assert.All(saved.DataSet.Rows, row => { Assert.Empty(row.Quality); Assert.Equal(ReadingQualityState.Unobserved, row.QualityState); });
        }
    }

}

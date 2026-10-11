using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class SampleResistanceExecutionTests
{
    [Fact]
    public async Task ThresholdSwitchSampleAnalysisRetainsScientificAndTerminalProvenanceAfterRestart()
    {
        using var folder = new TemporaryFolder(); var b = new Bench();
        var sample = SampleResistanceAnalysisPlan.Create(Recipe(Configuration(SampleTopology.HallBar, Geometry)));
        var root = new SequenceStep(new("switched_root"), [new AssignValueStep(new("selector_seed"), new("selector"), new ConstantValue(new(0, PhysicalUnit.Dimensionless))),
            new ThresholdSwitchStep(new("choose_sample"), new("selector"), [sample.Root, new DelayStep(new("unused"), TimeSpan.Zero)], [new(0, PhysicalUnit.Dimensionless)])]);
        await b.Runs.StartPlanAsync(new("switched sample", root, sample.Preparations, sample.Metadata, sample.Routing, sample.CurrentSourcePreparations), Token);
        var snapshot = b.Runs.LatestDataSet!.Capture(); Assert.Equal(3, snapshot.Rows.Count); Assert.NotNull(snapshot.Rows[^1].Analysis);
        await using (var archive = new FileRunArchive(folder.Path))
        {
            await archive.OnStartedAsync(snapshot with { State = MeasurementRunState.Running, FinishedAt = null, Rows = [], Blackboard = null }, Token);
            foreach (var row in snapshot.Rows) await archive.OnRowAsync(snapshot.Id, row, Token);
            await archive.OnFinalizedAsync(snapshot, Token);
        }
        await using var fresh = new FileRunArchive(folder.Path); var saved = await fresh.LoadAsync(snapshot.Id, Token);
        Assert.Equal(snapshot.Rows[^1].Values, saved.DataSet.Rows[^1].Values); Assert.Equal(snapshot.Rows[^1].Quality, saved.DataSet.Rows[^1].Quality);
        Assert.Equal(snapshot.Blackboard!.Entries.OrderBy(p => p.Key.Value, StringComparer.Ordinal),
            saved.DataSet.Blackboard!.Entries.OrderBy(p => p.Key.Value, StringComparer.Ordinal));
        Assert.False(b.Runs.HasActiveRun);
    }
}

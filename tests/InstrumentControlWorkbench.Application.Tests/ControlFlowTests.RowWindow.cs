using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task RowWindow_DurableRunKeepsBoundedTailAndFreshArchiveRetainsEveryOriginalRow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "row-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(); var runs = new MeasurementRunService(b.Registry, b.Hub, [archive], rowRetention: new(3));
                var body = new ForEachStep(new("loop"), new("requested"), new ScanValues(Enumerable.Range(0, 20).Select(value => new Quantity(value, PhysicalUnit.Dimensionless))), b.ReadField("field")); await runs.StartPlanAsync(new("bounded row run", body), Token); var data = runs.LatestDataSet!; id = data.Id;
                Assert.Equal(20, data.TotalRowCount); Assert.Equal(17, data.RowOffset); Assert.Equal(3, data.Rows.Count); Assert.Equal(new[] { 17.0, 18.0, 19.0 }, data.Rows.Select(row => row.Values[new("requested")].Value)); var snapshot = data.Capture(); Assert.Equal(20, snapshot.TotalRowCount); Assert.False(snapshot.HasCompleteRows); Assert.NotNull(snapshot.RowWindow); Assert.Equal(20, (await archive.ReadMetadataAsync(id, Token)).RowCount); Assert.False(runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
            }
            await using var fresh = new FileRunArchive(folder); var loaded = await fresh.LoadAsync(id, Token); Assert.Equal(20, loaded.DataSet.Rows.Count); Assert.True(loaded.DataSet.HasCompleteRows); Assert.Equal(0, loaded.DataSet.Rows[0].Values[new("requested")].Value); Assert.Equal(19, loaded.DataSet.Rows[^1].Values[new("requested")].Value);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("row")]
    [InlineData("prefix")]
    [InlineData("full")]
    [InlineData("offset")]
    [InlineData("encoding")]
    public async Task RowWindow_FinalizationRejectsTamperedBoundedWindowOrDigest(string damage)
    {
        var folder = Path.Combine(Path.GetTempPath(), "window-forgery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(folder); var id = RunId.New(); var column = new ParameterDescriptor(new("value"), "Value", PhysicalUnit.Volt, ParameterAccess.Read); var data = new RunDataSet(id, "window", DateTimeOffset.UtcNow, new([new DataColumn(column)]), new Dictionary<string, Quantity>(), [], rowEncoding: archive, retention: new(2)); await archive.OnStartedAsync(data.Capture(), Token);
            for (var index = 0; index < 5; index++) { var row = new MeasurementDataRow(DateTimeOffset.UtcNow, new Dictionary<ParameterId, Quantity> { [column.Id] = new(index, PhysicalUnit.Volt) }); data.Append(row); await archive.OnRowAsync(id, row, Token); }
            data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow); var snapshot = data.Capture(); var proof = snapshot.RowWindow!;
            var forgedProof = new RunRowWindowProof(damage == "encoding" ? "foreign" : proof.EncodingId, damage == "offset" ? proof.RowOffset + 1 : proof.RowOffset, proof.TotalRows, damage == "prefix" ? RunRowWindowProof.EmptyHash : proof.PrefixSha256, damage == "full" ? RunRowWindowProof.EmptyHash : proof.FullSha256);
            var rows = snapshot.Rows.ToArray(); if (damage == "row") rows[0] = new(rows[0].Timestamp, new Dictionary<ParameterId, Quantity> { [column.Id] = new(999, PhysicalUnit.Volt) });
            await Assert.ThrowsAsync<InvalidOperationException>(() => archive.OnFinalizedAsync(snapshot with { RowWindow = forgedProof, Rows = rows }, Token)); Assert.False(File.Exists(Path.Combine(folder, id.Value.ToString("N"), "final.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task RowWindow_BoundedWindowReconcilesAValidUndeliveredTailAfterSafeCompletion()
    {
        var folder = Path.Combine(Path.GetTempPath(), "window-tail-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                id = RunId.New(); var column = new ParameterDescriptor(new("value"), "Value", PhysicalUnit.Volt, ParameterAccess.Read); var data = new RunDataSet(id, "window", DateTimeOffset.UtcNow, new([new DataColumn(column)]), new Dictionary<string, Quantity>(), [], rowEncoding: archive, retention: new(2)); await archive.OnStartedAsync(data.Capture(), Token);
                for (var index = 0; index < 5; index++) { var row = new MeasurementDataRow(DateTimeOffset.UtcNow, new Dictionary<ParameterId, Quantity> { [column.Id] = new(index, PhysicalUnit.Volt) }); data.Append(row); if (index < 4) await archive.OnRowAsync(id, row, Token); }
                data.Complete(MeasurementRunState.Aborted, DateTimeOffset.UtcNow); await archive.OnFinalizedAsync(data.Capture(), Token);
            }
            await using var fresh = new FileRunArchive(folder); var reloaded = await fresh.LoadAsync(id, Token); Assert.Equal(5, reloaded.DataSet.Rows.Count); Assert.Equal(MeasurementRunState.Aborted, reloaded.DataSet.State);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task RowWindow_WithoutDurableEncoderCapacityFaultPreservesResultsAndReleasesDevices()
    {
        var b = new Bench(); var runs = new MeasurementRunService(b.Registry, b.Hub, rowRetention: new(2)); var body = new RepeatStep(new("loop"), 5, b.ReadField("field")); Assert.NotNull(await Record.ExceptionAsync(() => runs.StartPlanAsync(new("capacity", body), Token))); Assert.Equal(2, runs.LatestDataSet!.TotalRowCount); Assert.Equal(2, runs.LatestDataSet.Rows.Count); Assert.True(runs.LatestDataSet.Capture().HasCompleteRows); Assert.Equal(MeasurementRunState.Faulted, runs.LatestDataSet.State); Assert.False(runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task RowWindow_MemoryHistoryCapsRunCountAndTailPreviewWhileKeepingTotalCounts()
    {
        var history = new InMemoryRunHistory(2, 2); var snapshots = new List<RunDataSetSnapshot>();
        for (var run = 0; run < 3; run++)
        {
            var column = new ParameterDescriptor(new("value"), "Value", PhysicalUnit.Volt, ParameterAccess.Read); var data = new RunDataSet(RunId.New(), "history" + run, DateTimeOffset.UtcNow.AddSeconds(run), new([new DataColumn(column)]), new Dictionary<string, Quantity>(), []); await history.OnStartedAsync(data.Capture(), Token);
            for (var row = 0; row < 4; row++) { var value = new MeasurementDataRow(DateTimeOffset.UtcNow, new Dictionary<ParameterId, Quantity> { [column.Id] = new(row, PhysicalUnit.Volt) }); data.Append(value); await history.OnRowAsync(data.Id, value, Token); }
            data.Complete(MeasurementRunState.Completed, data.StartedAt.AddSeconds(1)); var snapshot = data.Capture(); snapshots.Add(snapshot); await history.OnFinalizedAsync(snapshot, Token);
        }
        var runs = history.Runs; Assert.Equal(2, runs.Count); Assert.DoesNotContain(runs, run => run.Id == snapshots[0].Id); Assert.All(runs, run => { Assert.Equal(4, run.TotalRowCount); Assert.Equal(2, run.RowOffset); Assert.Equal(2, run.Rows.Count); Assert.Null(run.RowWindow); Assert.False(run.HasCompleteRows); }); Assert.Equal(4, snapshots[2].Rows.Count);
    }
    [Fact]
    public async Task RowWindow_ByteBoundedHistoryPreviewIsExplicitAndCannotFinalizeAnArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "preview-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archive = new FileRunArchive(folder); var column = new ParameterDescriptor(new("value"), "Value", PhysicalUnit.Volt, ParameterAccess.Read); var data = new RunDataSet(RunId.New(), "preview", DateTimeOffset.UtcNow, new([new DataColumn(column)]), new Dictionary<string, Quantity>(), []); var history = new InMemoryRunHistory(2, 1); await archive.OnStartedAsync(data.Capture(), Token); await history.OnStartedAsync(data.Capture(), Token);
            for (var row = 0; row < 3; row++) { var value = new MeasurementDataRow(DateTimeOffset.UtcNow, new Dictionary<ParameterId, Quantity> { [column.Id] = new(row, PhysicalUnit.Volt) }); data.Append(value); await archive.OnRowAsync(data.Id, value, Token); await history.OnRowAsync(data.Id, value, Token); }
            data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow); await history.OnFinalizedAsync(data.Capture(), Token); var preview = Assert.Single(history.Runs); Assert.Equal(3, preview.TotalRowCount); Assert.Null(preview.RowWindow); await Assert.ThrowsAsync<InvalidOperationException>(() => archive.OnFinalizedAsync(preview, Token));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task RowWindow_RollingCalculatedRowsKeepInputIndicesAndFreshArchiveReplayAcrossEviction()
    {
        var folder = Path.Combine(Path.GetTempPath(), "calculated-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(folder))
            {
                var b = new Bench(); var runs = new MeasurementRunService(b.Registry, b.Hub, [archive], rowRetention: new(1)); var scan = new ScanStep(new("scan"), new(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id), new("requested"), new ScanValues([new(.01, PhysicalUnit.Volt), new(.02, PhysicalUnit.Volt), new(.03, PhysicalUnit.Volt)]), TimeSpan.Zero, CalculatedPair(b)); await runs.StartPlanAsync(b.Powered(scan), Token); var data = runs.LatestDataSet!; id = data.Id; Assert.Equal(6, data.TotalRowCount); Assert.Equal(5, data.RowOffset); Assert.Equal(5, Assert.Single(data.Rows).Calculation!.InputRow); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            }
            await using var fresh = new FileRunArchive(folder); var loaded = await fresh.LoadAsync(id, Token); CheckCalculatedRows(loaded.DataSet.Rows);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

}

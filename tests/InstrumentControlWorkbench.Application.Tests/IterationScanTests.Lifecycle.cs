using System.Text.Json.Nodes;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;
using InstrumentControlWorkbench.Infrastructure.Mock;
using InstrumentControlWorkbench.Infrastructure.Plans;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class IterationScanTests
{
    [Fact]
    public async Task NestedRepositoryFixtureSavesReopensArchivesAndExecutesAgainWithOptionalAxes()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"iteration-archive-{Guid.NewGuid():N}");
        try
        {
            RunId id;
            await using (var archive = new FileRunArchive(Path.Combine(folder, "runs")))
            {
                var b = new Bench(archive);
                var document = await b.Workspace.ImportAsync(Path.Combine(AppContext.BaseDirectory, "plans", "ppms-number-time-import.json"), b.Bindings, Token);
                Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
                Assert.Empty(document.Plan.Preparations); Assert.Contains("DelayBeforePoint", document.Text);
                var nativePath = Path.Combine(folder, "converted.plan.json"); await b.Workspace.SaveAsync(nativePath, document.Text, Token);
                await b.Runs.StartPlanAsync((await b.Workspace.LoadAsync(nativePath, Token)).Plan, Token);
                var data = b.Runs.LatestDataSet!; id = data.Id;
                Assert.Equal(5, data.Rows.Count); Assert.Single(data.Instruments);
                Assert.Equal(new[] { 0.0, 0, 2, 2 }, data.Rows.Take(4).Select(r => r.Values[new("ppms.root.0.axis")].Value));
                Assert.Equal(new[] { 0.01, 0.02, 0.01, 0.02 }, data.Rows.Take(4).Select(r => r.Values[new("ppms.root.0.0.axis")].Value));
                Assert.All(data.Rows, data.Schema.ValidateRow);
                Assert.DoesNotContain(data.Rows[^1].Values.Keys, k => k.Value.EndsWith(".axis", StringComparison.Ordinal) || k.Value.EndsWith(".index", StringComparison.Ordinal) || k.Value.EndsWith(".total", StringComparison.Ordinal));
                Assert.Contains(new ParameterId("ppms.root.0.0.axis"), data.Schema.Columns.Single(c => c.Parameter.Id == new ParameterId("ppms.root.0.0.0.value")).DependsOn);
                Assert.Equal(2, data.Plan!.Metadata["ppms.root.0.0.source_line"].Value);
                Assert.Equal(MeasurementRunState.Completed, data.State);
                Assert.False(b.Runs.HasActiveRun);
            }
            await using var reopened = new FileRunArchive(Path.Combine(folder, "runs"));
            var saved = await reopened.LoadAsync(id, Token); Assert.Equal(5, saved.DataSet.Rows.Count);
            var root = Assert.IsType<SequenceStep>(saved.DataSet.Plan!.Root);
            Assert.Equal(IterationTiming.DelayBeforePoint, Assert.IsType<SequenceStep>(Assert.IsType<ForEachStep>(root.Children[0]).Body).Children.OfType<ForEachStep>().Single().Timing);
            var fresh = new Bench(); await fresh.Runs.StartPlanAsync(saved.DataSet.Plan, Token);
            Assert.Equal(5, fresh.Runs.LatestDataSet!.Rows.Count);
            var csv = Path.Combine(folder, "points.csv"); await reopened.ExportCsvAsync(id, csv, Token);
            var exported = await File.ReadAllTextAsync(csv); Assert.Contains("ppms.root.0.axis", exported); Assert.Contains("ppms.root.0.0.axis", exported);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task PauseFreezesPointDelayTurnsOutputOffRetainsGroupAndResumesRemainingWait()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var body = new SequenceStep(new("point"), [b.ReadSource("electrical"), new ReadStep(new("field_read"), [new(new("field_value"), new(b.Field.Definition.Device.Id, b.Field.Definition.Readback.Id))])]);
        var plan = b.Powered(new ForEachStep(new("time"), new("delay"), new([new(0, PhysicalUnit.Second), new(0.5, PhysicalUnit.Second)]), body, IterationTiming.DelayBeforePoint));
        var task = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepCompleted, new("point")); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            Assert.True(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(0, PhysicalUnit.Volt), Token));
            var count = b.Runs.LatestDataSet!.Rows.Count; Assert.Equal(2, count);
            await Task.Delay(650); Assert.Equal(count, b.Runs.LatestDataSet.Rows.Count); Assert.False(task.IsCompleted);
            b.Runs.Resume(); await Until(events, WorkbenchNotificationKind.RunResumed);
            Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            await Task.Delay(60); Assert.Equal(count, b.Runs.LatestDataSet.Rows.Count);
            await task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(4, b.Runs.LatestDataSet.Rows.Count);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.False(b.Runs.HasActiveRun);
        }
        finally { if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortOrCancellationDuringDelayDoesNotMeasureOrLeaveOutputEnabled(bool externalCancellation)
    {
        var b = new Bench(); using var events = b.Hub.Subscribe(); using var cancellation = new CancellationTokenSource();
        var plan = b.Powered(new ForEachStep(new("time"), new("delay"), new([new(30, PhysicalUnit.Second)]), b.ReadSource("point"), IterationTiming.DelayBeforePoint));
        var task = b.Runs.StartPlanAsync(plan, cancellation.Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("time")); Assert.True((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            if (externalCancellation) cancellation.Cancel(); else b.Runs.Abort();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State);
            Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun);
            Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
        }
        finally { if (!task.IsCompleted) { b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    [Fact]
    public async Task ListenerFaultStopsTimedIteratorAndPreservesOnlyCommittedFirstPoint()
    {
        var listener = new RefusingRowListener(); var b = new Bench(listener);
        var plan = b.Powered(new ForEachStep(new("time"), new("delay"), new([new(0, PhysicalUnit.Second), new(30, PhysicalUnit.Second)]), b.ReadSource("point"), IterationTiming.DelayBeforePoint));
        var error = await Record.ExceptionAsync(() => b.Runs.StartPlanAsync(plan, Token)); Assert.NotNull(error); Assert.Contains("refused point", error.Message);
        Assert.Equal(1, listener.Rows); Assert.Single(b.Runs.LatestDataSet!.Rows);
        Assert.Equal(0, b.Runs.LatestDataSet.Rows[0].Values[new("delay")].Value); Assert.Equal(1, b.Runs.LatestDataSet.Rows[0].Values[new("delay.index")].Value);
        Assert.Equal(MeasurementRunState.Faulted, b.Runs.LatestDataSet.State); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
        Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task IteratorOnlyPlanAddsNoDeviceClaimsAndEmitsNoSyntheticRows()
    {
        var b = new Bench();
        var plan = b.Import(Node("scan", "scan_time", new() { ["points"] = new JsonArray(0, 0.001) }));
        await b.Runs.StartPlanAsync(plan, Token);
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Empty(b.Runs.LatestDataSet.Instruments);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).Connected); Assert.False((await b.Field.GetSnapshotAsync(Token)).Connected);
    }

    [Fact]
    public async Task FailedHoldOnTimedCancellationRetainsWholeGroupUntilExplicitRecovery()
    {
        var field = new RefusingHoldAxis(MockEnvironmentCatalog.Field()); var b = new Bench(field: field);
        using var events = b.Hub.Subscribe();
        var plan = b.Powered(new ForEachStep(new("time"), new("delay"), new([new(30, PhysicalUnit.Second)]),
            new SequenceStep(new("body"), [b.ReadSource("electrical"), new ReadStep(new("field_read"), [new(new("field_value"), new(field.Definition.Device.Id, field.Definition.Readback.Id))])]), IterationTiming.DelayBeforePoint));
        var task = b.Runs.StartPlanAsync(plan, Token);
        try
        {
            await Until(events, WorkbenchNotificationKind.StepStarted, new("time")); b.Runs.Abort();
            Assert.NotNull(await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled);
            Assert.True(b.Runs.HasActiveRun); Assert.Equal(RunExecutionPhase.SafetyRecoveryRequired, b.Runs.Phase);
            Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow)); Assert.True(b.Leases.IsLeased(field.Definition.Device.Id, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Source.Id, Keithley2450Parameters.SourceVoltage.Id, new(0, PhysicalUnit.Volt), Token));
            field.Refuse = false; await b.Runs.RecoverSafetyAsync(Token);
            Assert.False(b.Runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow));
            Assert.False(b.Leases.IsLeased(field.Definition.Device.Id, DateTimeOffset.UtcNow)); Assert.True(field.HoldCalls >= 2);
        }
        finally { if (!task.IsCompleted) { field.Refuse = false; b.Runs.Abort(); await task.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }

    private sealed class RefusingHoldAxis(MockAxisController inner) : IAxisControllerPort
    {
        public bool Refuse { get; set; } = true;
        public int HoldCalls { get; private set; }
        public AxisControllerDefinition Definition => inner.Definition;
        public Task<AxisControllerSnapshot> ConnectAsync(CancellationToken token) => inner.ConnectAsync(token);
        public Task<AxisControllerSnapshot> GetSnapshotAsync(CancellationToken token) => inner.GetSnapshotAsync(token);
        public Task SetTargetAsync(Quantity target, CancellationToken token) => inner.SetTargetAsync(target, token);
        public Task SetRateAsync(Quantity rate, CancellationToken token) => inner.SetRateAsync(rate, token);
        public Task DisconnectAsync(CancellationToken token) => inner.DisconnectAsync(token);
        public Task HoldAsync(CancellationToken token) { HoldCalls++; if (Refuse) throw new IOException("controller hold refused"); return inner.HoldAsync(token); }
    }

    private sealed class RefusingRowListener : IRunResultListener
    {
        public int Rows { get; private set; }
        public Task OnStartedAsync(RunDataSetSnapshot dataSet, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnRowAsync(RunId runId, MeasurementDataRow row, CancellationToken cancellationToken) { Rows++; throw new IOException("listener refused point"); }
        public Task OnFinalizedAsync(RunDataSetSnapshot dataSet, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class ControlFlowTests
{
    [Fact]
    public async Task SlowDisplayAndFailedReliableObserverDoNotChangeArchiveOrSafetyFinalization()
    {
        var folder = Path.Combine(Path.GetTempPath(), "display-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            Core.Data.RunId id;
            var b = new Bench(); using var display = b.Hub.SubscribeDisplay(512); using var stalled = b.Hub.Subscribe(8);
            await using (var archive = new FileRunArchive(folder))
            {
                var runs = new MeasurementRunService(b.Registry, b.Hub, [archive], rowRetention: new(3));
                var body = new ForEachStep(new("loop"), new("requested"), new ScanValues(Enumerable.Range(0, 300).Select(i => new Quantity(i, PhysicalUnit.Dimensionless))), b.ReadField("field"));
                await runs.StartPlanAsync(new("slow display", body), Token); id = runs.LatestDataSet!.Id;
                Assert.Equal(MeasurementRunState.Completed, runs.LatestDataSet.State); Assert.Equal(300, runs.LatestDataSet.TotalRowCount); Assert.Equal(3, runs.LatestDataSet.Rows.Count);
                Assert.False(runs.HasActiveRun); Assert.False(b.Leases.IsLeased(b.Field.Definition.Device.Id, DateTimeOffset.UtcNow));
                Assert.InRange(display.Reader.Count, 1, 512); Assert.True(display.OmittedDisplayFacts > 0);
                var facts = new List<WorkbenchNotification>(); while (display.Reader.TryRead(out var fact)) facts.Add(fact);
                Assert.Contains(facts, fact => fact.Kind == WorkbenchNotificationKind.RunStarted);
                var terminal = Assert.Single(facts, fact => fact.Kind == WorkbenchNotificationKind.RunCompleted); Assert.Equal(300, terminal.AcquiredRowCount);
                Assert.True(facts.IndexOf(terminal) > facts.FindIndex(fact => fact.Kind == WorkbenchNotificationKind.RunFinalizing));
                Assert.False(display.Reader.Completion.IsCompleted);
                while (stalled.Reader.TryRead(out _)) { }
                await Assert.ThrowsAsync<NotificationLagException>(() => stalled.Reader.Completion);
            }
            await using var fresh = new FileRunArchive(folder); var loaded = await fresh.LoadAsync(id, Token);
            Assert.Equal(300, loaded.DataSet.Rows.Count); Assert.Equal(0, loaded.DataSet.Rows[0].Values[new("requested")].Value); Assert.Equal(299, loaded.DataSet.Rows[^1].Values[new("requested")].Value);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

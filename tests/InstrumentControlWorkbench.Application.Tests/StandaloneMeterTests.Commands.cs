using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed partial class StandaloneMeterTests
{
    [Fact]
    public async Task InputSelectionRefusalAndFailedAbortRetainWholeGroupUntilFreshRecovery()
    {
        var b = new Bench(hardwareMeter: true); b.Meter.IgnoreWrites = true; b.Meter.RefuseStop = true;
        await Assert.ThrowsAnyAsync<Exception>(() => b.Runs.StartPlanAsync(b.Plan(b.Write("input", Keithley2182Parameters.Channel, 2), b.Read()), Token));
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.True(b.Runs.HasActiveRun);
        Assert.True(b.Leases.IsLeased(b.Meter.Descriptor.Id, DateTimeOffset.UtcNow.AddYears(1))); Assert.True(b.Leases.IsLeased(b.Source.Id, DateTimeOffset.UtcNow.AddYears(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Meter.Descriptor.Id, Keithley2182Parameters.Channel.Id, new(1, PhysicalUnit.Dimensionless), Token));
        b.Meter.IgnoreWrites = false; b.Meter.RefuseStop = false; await b.Runs.RecoverSafetyAsync(Token, reconnect: true); Assert.False(b.Runs.HasActiveRun);
    }
    [Fact]
    public async Task CancellationDuringInputSelectionCommitsNoRowAndUsesIndependentCleanup()
    {
        var b = new Bench(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Meter.OnSelect = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var running = b.Runs.StartPlanAsync(b.Plan(b.Write("input", Keithley2182Parameters.Channel, 2), b.Read()), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); b.Runs.Abort(); await running;
        Assert.Empty(b.Runs.LatestDataSet!.Rows); Assert.Equal(MeasurementRunState.Aborted, b.Runs.LatestDataSet.State); Assert.True(b.Meter.LastStopTokenWasLive);
        Assert.False((await b.Source.GetSnapshotAsync(Token)).OutputEnabled); Assert.False(b.Runs.HasActiveRun); Assert.Equal(1, b.Meter.Settings.Channel);
    }
    [Fact]
    public async Task PausedRunOwnsBothInputsAndManualSelectionCannotAlterTheResumeState()
    {
        var b = new Bench(); using var events = b.Hub.Subscribe();
        var running = b.Runs.StartPlanAsync(b.Plan(b.Write("input", Keithley2182Parameters.Channel, 2), b.Read(), new Core.Experiments.DelayStep(new("wait"), TimeSpan.FromSeconds(30))), Token);
        await Until(events, WorkbenchNotificationKind.DataRowReceived); b.Runs.Pause(); await Until(events, WorkbenchNotificationKind.RunPaused);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Registry.WriteAsync(b.Meter.Descriptor.Id, Keithley2182Parameters.Channel.Id, new(1, PhysicalUnit.Dimensionless), Token));
        Assert.Equal(2, b.Meter.Settings.Channel); b.Runs.Abort(); await running; Assert.False(b.Runs.HasActiveRun);
    }
}

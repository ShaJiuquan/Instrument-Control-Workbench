using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class LiveDisplayTests
{
    [Fact]
    public async Task LivePlotAndTableKeepOnlyTheirTailWhileTotalAndRowNumbersRemainAccurate()
    {
        var hub = new NotificationHub(); var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        await using var vm = new MainWindowViewModel(manager, new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub), hub);
        for (var i = 1; i <= MainWindowViewModel.MaximumPlotPoints + 50; i++)
            vm.ApplyFact(new(WorkbenchNotificationKind.MeasurementPointReceived, "point", Point: new(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(i), i, 1, "V", "A"), AcquiredRowCount: i * 2));
        Assert.Equal(MainWindowViewModel.MaximumPlotPoints, vm.PlotPoints.Count); Assert.Equal(51, vm.PlotPoints[0].SourceValue);
        Assert.Equal(MainWindowViewModel.MaximumRecentPoints, vm.RecentPoints.Count); Assert.Equal((MainWindowViewModel.MaximumPlotPoints + 50) * 2, vm.CompletedPoints);
        Assert.Equal(vm.CompletedPoints.ToString("0000"), vm.RecentPoints[0].Index); Assert.Equal(200, vm.EventLog.Count);
        vm.ApplyFact(new(WorkbenchNotificationKind.RunCompleted, "finished", AcquiredRowCount: 9000));
        Assert.Equal(9000, vm.CompletedPoints); Assert.Equal(OperatorRunState.Completed, vm.RunState); Assert.Contains("最近 4096 点", vm.LiveDisplaySummary);
    }

    [Fact]
    public async Task ObservationGapDisablesNewWorkAndResumeButStillAllowsOwnedRunAbortAndSafeClose()
    {
        var hub = new NotificationHub(); var source = new MockKeithley2450SourceMeter(); var manager = new InstrumentManager(source, hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub);
        await using var vm = new MainWindowViewModel(manager, runs, hub);
        vm.SweepPointsText = "2"; vm.DwellSecondsText = "10";
        using var events = hub.Subscribe(); var running = vm.StartRunCommand.ExecuteAsync(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await events.Reader.ReadAsync(timeout.Token)).Kind != WorkbenchNotificationKind.MeasurementPointReceived) { }
        vm.ApplyObservationFailure(new(512));
        Assert.True(vm.NotificationObservationLost); Assert.False(vm.CanEdit); Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.StartRunCommand.CanExecute(null)); Assert.False(vm.ResumeRunCommand.CanExecute(null)); Assert.False(vm.PauseRunCommand.CanExecute(null));
        Assert.Contains("过期", vm.StatusText); Assert.Contains("重新确认", vm.SafetyText); Assert.Contains("停止运行", vm.OperationError);
        vm.ApplyFact(new(WorkbenchNotificationKind.RunCompleted, "stale completion", AcquiredRowCount: 99)); Assert.Equal(0, vm.CompletedPoints);
        Assert.True(vm.AbortRunCommand.CanExecute(null)); vm.AbortRunCommand.Execute(null); await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(runs.HasActiveRun); Assert.False((await source.GetSnapshotAsync(CancellationToken.None)).OutputEnabled);
    }

    [Fact]
    public async Task CloseCancelsAnUnservicedDispatcherDeliveryWithoutWaitingForANativeWindow()
    {
        var hub = new NotificationHub(); var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var vm = new MainWindowViewModel(manager, new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub), hub);
        await hub.PublishAsync(new(WorkbenchNotificationKind.RunStarted, "queued"));
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        vm.ApplyFact(new(WorkbenchNotificationKind.RunCompleted, "late", AcquiredRowCount: 99));
        Assert.Equal(0, vm.CompletedPoints);
    }
}

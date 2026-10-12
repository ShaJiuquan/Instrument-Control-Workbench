using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class LateOperatorCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateControlInvocationDoesNotMutateTerminalStateOrRequestAnAbsentRun(bool busyPresentation)
    {
        var hub = new NotificationHub(); var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub);
        await using var vm = new MainWindowViewModel(manager, runs, hub);
        // Presentation may still be busy while backend finalization has finished.
        vm.RunState = OperatorRunState.Completed; vm.IsBusy = busyPresentation;
        Assert.False(vm.PauseRunCommand.CanExecute(null)); Assert.False(vm.ResumeRunCommand.CanExecute(null)); Assert.False(vm.AbortRunCommand.CanExecute(null));
        vm.PauseRunCommand.Execute(null); vm.ResumeRunCommand.Execute(null); vm.AbortRunCommand.Execute(null);
        Assert.Equal(OperatorRunState.Completed, vm.RunState); Assert.False(vm.PauseRequested); Assert.False(runs.HasActiveRun);
        Assert.Equal("", vm.OperationError); Assert.DoesNotContain(vm.EventLog, e => e.Contains("操作员请求停止", StringComparison.Ordinal));
        vm.IsBusy = false;
    }
}

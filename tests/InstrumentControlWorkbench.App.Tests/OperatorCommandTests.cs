using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class OperatorCommandTests
{
    [Fact]
    public async Task BackendOwnershipLocksManualCommandsEvenWhenTheUiDidNotStartTheRun()
    {
        var hub = new NotificationHub();
        var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var service = new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub);
        await using var vm = new MainWindowViewModel(manager, service, hub);
        var running = service.StartIvSweepAsync(new("external run", 0, 0.1, 3, 105e-6, 10), CancellationToken.None);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanEdit);
        Assert.False(vm.StartRunCommand.CanExecute(null));
        Assert.False(vm.ConnectCommand.CanExecute(null));
        service.Abort();
        await running;
        Assert.True(vm.CanEdit);
    }

    private static MainWindowViewModel Create()
    {
        var hub = new NotificationHub();
        var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        return new MainWindowViewModel(manager, new MeasurementRunService(manager, new InstrumentLeaseService(TimeSpan.FromSeconds(5)), hub), hub);
    }

    [Theory]
    [InlineData("NaN", "0.8", "21", "0.1", "105")]
    [InlineData("-0.2", "Infinity", "21", "0.1", "105")]
    [InlineData("-0.2", "0.8", "1", "0.1", "105")]
    [InlineData("-0.2", "0.8", "21", "0", "105")]
    [InlineData("-0.2", "0.8", "21", "0.1", "0")]
    [InlineData("-211", "0.8", "21", "0.1", "105")]
    [InlineData("-0,2", "0.8", "21", "0.1", "105")]
    public void InvalidInputIsRejectedWithoutClamping(string start, string stop, string points, string dwell, string limit)
    {
        Assert.False(SweepConfiguration.TryCreate(start, stop, points, dwell, limit, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EditorConvertsMicroAmpsToAmps()
    {
        Assert.True(SweepConfiguration.TryCreate("-0.2", "0.8", "21", "0.1", "105", out var recipe, out _));
        Assert.Equal(105e-6, recipe.ComplianceAmps, 12);
    }

    [Fact]
    public async Task InvalidEditDisablesStartAndKeepsEnteredText()
    {
        await using var vm = Create();
        Assert.True(vm.StartRunCommand.CanExecute(null));
        vm.StartVoltageText = "bad";
        Assert.False(vm.StartRunCommand.CanExecute(null));
        Assert.Equal("bad", vm.StartVoltageText);
        Assert.True(vm.HasValidationError);
        vm.StartVoltageText = "-0.2";
        Assert.True(vm.StartRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunningLocksConfigurationAndAllowsOnlyOneStopRequest()
    {
        await using var vm = Create();
        var running = vm.StartRunCommand.ExecuteAsync(null);
        Assert.False(vm.CanEdit);
        Assert.False(vm.StartRunCommand.CanExecute(null));
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.AbortRunCommand.CanExecute(null));
        vm.AbortRunCommand.Execute(null);
        Assert.Equal(OperatorRunState.Stopping, vm.RunState);
        Assert.False(vm.AbortRunCommand.CanExecute(null));
        await running;
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task ConnectedDeviceCannotBeConnectedAgainThroughTheUi()
    {
        await using var vm = Create();
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.Connected);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.StartRunCommand.CanExecute(null));
    }
}

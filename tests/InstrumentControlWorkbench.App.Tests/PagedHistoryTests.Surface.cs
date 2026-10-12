using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Instruments;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Application.Notifications;
using InstrumentControlWorkbench.Infrastructure.Data;
using InstrumentControlWorkbench.Infrastructure.Drivers;

namespace InstrumentControlWorkbench.App.Tests;

public sealed partial class PagedHistoryTests
{
    [Fact]
    public async Task CatalogBindingResetDoesNotDiscardSameRunTrustedPlotOrCustomExportPath()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(Path.Combine(folder.Path, "runs")); await SaveAsync(archive, 2);
        await using var vm = new PagedRunHistoryViewModel(new(archive), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null); await vm.OpenCommand.ExecuteAsync(null);
        var trusted = vm.Plot; var selected = vm.SelectedRun!.Id; vm.ExportPath = Path.Combine(folder.Path, "custom.csv");
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Runs)) vm.SelectedRun = null; };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(selected, vm.SelectedRun!.Id); Assert.Same(trusted, vm.Plot); Assert.Equal(Path.Combine(folder.Path, "custom.csv"), vm.ExportPath);
    }
    [Fact]
    public async Task ShellUsesBoundedPagesAndFullExportWithoutFullCatalogOrRunLoad()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(Path.Combine(folder.Path, "runs")); var id = await SaveAsync(archive, 505);
        var gate = new ReadGateArchive(archive); var hub = new NotificationHub(); var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var runs = new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub);
        await using var shell = new MainWindowViewModel(manager, runs, hub, history: new(gate), exportDirectory: Path.Combine(folder.Path, "exports"));
        await shell.OpenHistoryCommand.ExecuteAsync(null); var vm = shell.History!; Assert.Equal(id, vm.SelectedRun!.Id);
        await vm.OpenCommand.ExecuteAsync(null); Assert.Equal(500, vm.Rows.Count); Assert.False(vm.ConnectRecords); Assert.Equal(0, gate.FullLoadCalls); Assert.Equal(0, gate.FullCatalogCalls);
        await vm.NextPlotCommand.ExecuteAsync(null); Assert.Equal(5, vm.Rows.Count); Assert.True(vm.PreviousPlotCommand.CanExecute(null));
        await vm.PreviousPlotCommand.ExecuteAsync(null); Assert.Equal(500, vm.Rows.Count); Assert.Equal(1, vm.Rows[0].RowIndex);
        await vm.ExportCommand.ExecuteAsync(null); Assert.Equal(id, gate.LastExportId); Assert.Contains("完整运行", vm.Message);
        Assert.Contains("504", await File.ReadAllTextAsync(vm.ExportPath)); Assert.Equal(0, gate.FullLoadCalls);
    }
    [Fact]
    public async Task ChangingSelectionDuringExportKeepsCapturedDestinationAndDrainsBeforeNextRead()
    {
        using var folder = new TemporaryFolder(); await using var archive = new FileRunArchive(Path.Combine(folder.Path, "runs"));
        await SaveAsync(archive, 2, "first"); await SaveAsync(archive, 2, "second");
        var gate = new ReadGateArchive(archive) { BlockExport = true }; await using var vm = new PagedRunHistoryViewModel(new(gate), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null); var old = vm.SelectedRun!.Id; var path = vm.ExportPath;
        var operation = vm.ExportCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = vm.Runs.Single(r => r.Id != old); vm.SelectedRun = other;
        Assert.True(gate.LastToken.IsCancellationRequested); Assert.False(vm.RefreshCommand.CanExecute(null)); Assert.NotEqual(path, vm.ExportPath);
        gate.Release.TrySetResult(); await operation; Assert.Equal(old, gate.LastExportId); Assert.Equal(path, gate.LastExportPath);
        Assert.Equal(other.Id, vm.SelectedRun!.Id); Assert.DoesNotContain("已重新校验并导出", vm.Message); Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.Equal(0, gate.DisposeCalls); Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task ShellCloseDrainsCanceledExportBeforeDisposingItsSharedArchive()
    {
        using var folder = new TemporaryFolder(); var archive = new FileRunArchive(Path.Combine(folder.Path, "runs")); await SaveAsync(archive, 2);
        var gate = new ReadGateArchive(archive) { BlockExport = true }; var hub = new NotificationHub(); var manager = new InstrumentManager(new MockKeithley2450SourceMeter(), hub);
        var shell = new MainWindowViewModel(manager, new MeasurementRunService(manager, new(TimeSpan.FromSeconds(5)), hub), hub, history: new(gate), exportDirectory: Path.Combine(folder.Path, "exports"));
        await shell.History!.RefreshCommand.ExecuteAsync(null); var export = shell.History.ExportCommand.ExecuteAsync(null); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = shell.DisposeAsync().AsTask(); Assert.True(gate.LastToken.IsCancellationRequested); Assert.False(close.IsCompleted); Assert.Equal(0, gate.DisposeCalls);
        gate.Release.TrySetResult(); await export; await close; Assert.Equal(1, gate.DisposeCalls); Assert.False(shell.History.RefreshCommand.CanExecute(null));
    }
}

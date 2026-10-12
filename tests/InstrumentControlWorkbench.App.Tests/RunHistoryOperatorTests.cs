using InstrumentControlWorkbench.App.ViewModels;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Infrastructure.Data;

namespace InstrumentControlWorkbench.App.Tests;

public sealed class RunHistoryOperatorTests
{
    [Fact]
    public async Task ReopenedHistoryOpensTypedRowsAndExportsSampleMetadata()
    {
        using var folder = new TemporaryFolder();
        var data = DataSet();
        await using (var writer = new FileRunArchive(Path.Combine(folder.Path, "runs")))
        {
            await writer.OnStartedAsync(data.Capture(), CancellationToken.None);
            data.Append(Row());
            await writer.OnRowAsync(data.Id, data.Rows[0], CancellationToken.None);
            data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
            await writer.OnFinalizedAsync(data.Capture(), CancellationToken.None);
        }
        await using var reader = new FileRunArchive(Path.Combine(folder.Path, "runs"));
        var vm = new PagedRunHistoryViewModel(new(reader), Path.Combine(folder.Path, "exports"));
        await using var owner = vm;
        Assert.False(vm.OpenCommand.CanExecute(null));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(data.Id, vm.SelectedRun!.Id);
        Assert.True(vm.SelectedRun.IsFinalized); Assert.Equal(MeasurementRunState.Completed, vm.SelectedRun.State);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Contains("样品甲", vm.Details);
        Assert.Equal(PhysicalUnit.Volt, Assert.Single(vm.Rows).X!.Value.Unit);
        Assert.Contains("校验通过", vm.Message);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("样品甲", await File.ReadAllTextAsync(vm.ExportPath));
        await vm.DisposeAsync();
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task InterruptedRecordIsVisibleAsUnfinalizedWithUnknownPhysicalState()
    {
        using var folder = new TemporaryFolder();
        var data = DataSet();
        await using (var writer = new FileRunArchive(Path.Combine(folder.Path, "runs")))
        {
            await writer.OnStartedAsync(data.Capture(), CancellationToken.None);
            await writer.OnRowAsync(data.Id, Row(), CancellationToken.None);
        }
        await File.AppendAllTextAsync(Path.Combine(folder.Path, "runs", data.Id.Value.ToString("N"), "rows.ndjson"), "{unfinished");
        await using var reader = new FileRunArchive(Path.Combine(folder.Path, "runs"));
        await using var vm = new PagedRunHistoryViewModel(new(reader), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.SelectedRun!.IsFinalized);
        Assert.Null(vm.SelectedRun.RowCount);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Single(vm.Rows);
        Assert.Contains("不完整记录已排除", vm.Message);
        Assert.Contains("设备当前状态未知", vm.RecordStatus);
    }

    [Fact]
    public async Task CorruptRowsReportFailureAndExistingExportSurvives()
    {
        using var folder = new TemporaryFolder();
        var data = DataSet();
        await using var archive = new FileRunArchive(Path.Combine(folder.Path, "runs"));
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        data.Append(Row());
        await archive.OnRowAsync(data.Id, data.Rows[0], CancellationToken.None);
        data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(folder.Path, "runs", data.Id.Value.ToString("N"), "rows.ndjson"), "corrupt");
        await using var vm = new PagedRunHistoryViewModel(new(archive), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Contains("读取失败", vm.Message);
        Assert.Empty(vm.Rows);
        Directory.CreateDirectory(Path.GetDirectoryName(vm.ExportPath)!);
        await File.WriteAllTextAsync(vm.ExportPath, "previous export");
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("导出失败", vm.Message);
        Assert.Equal("previous export", await File.ReadAllTextAsync(vm.ExportPath));
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenUsesCurrentTerminalStateWhenCatalogWasListedDuringAcquisition()
    {
        using var folder = new TemporaryFolder();
        var data = DataSet();
        await using var archive = new FileRunArchive(Path.Combine(folder.Path, "runs"));
        await archive.OnStartedAsync(data.Capture(), CancellationToken.None);
        await using var vm = new PagedRunHistoryViewModel(new(archive), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.SelectedRun!.IsFinalized);
        data.Append(Row());
        await archive.OnRowAsync(data.Id, data.Rows[0], CancellationToken.None);
        data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.True(vm.Plot!.Metadata.IsFinalized); Assert.Equal(MeasurementRunState.Completed, vm.Plot.Metadata.Header.State);
        Assert.Single(vm.Rows);
    }

    private static RunDataSet DataSet() => new(RunId.New(), "saved run", DateTimeOffset.UtcNow,
        new([new(new ParameterDescriptor(new("voltage"), "Voltage", PhysicalUnit.Volt, ParameterAccess.Read))]),
        new Dictionary<string, Quantity>(), [], metadata: new(sampleName: "样品甲"));
    private static MeasurementDataRow Row() => new(DateTimeOffset.UtcNow,
        new Dictionary<ParameterId, Quantity> { [new("voltage")] = new(0.1, PhysicalUnit.Volt) });
    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icw-history-ui-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}

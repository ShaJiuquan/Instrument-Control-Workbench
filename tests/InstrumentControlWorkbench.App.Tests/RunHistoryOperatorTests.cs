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
        var vm = new RunHistoryViewModel(new(new FileRunArchive(Path.Combine(folder.Path, "runs"))), Path.Combine(folder.Path, "exports"));
        await using var owner = vm;
        Assert.False(vm.OpenCommand.CanExecute(null));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(data.Id, vm.SelectedRun!.Metadata.Header.Id);
        Assert.Equal("完成", vm.SelectedRun.Status);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Contains("样品甲", vm.Details);
        Assert.Contains("Volt", Assert.Single(vm.Rows).Values);
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
        await using var vm = new RunHistoryViewModel(new(new FileRunArchive(Path.Combine(folder.Path, "runs"))), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("未结束记录", vm.SelectedRun!.Status);
        Assert.Contains("? 行", vm.SelectedRun.Summary);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Single(vm.Rows);
        Assert.Contains("不完整记录被排除", vm.Message);
        Assert.Contains("设备当前状态未知", vm.Message);
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
        await using var vm = new RunHistoryViewModel(new(archive), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Contains("数据打开失败", vm.Message);
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
        await using var vm = new RunHistoryViewModel(new(archive), Path.Combine(folder.Path, "exports"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("未结束记录", vm.SelectedRun!.Status);
        data.Append(Row());
        await archive.OnRowAsync(data.Id, data.Rows[0], CancellationToken.None);
        data.Complete(MeasurementRunState.Completed, DateTimeOffset.UtcNow);
        await archive.OnFinalizedAsync(data.Capture(), CancellationToken.None);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Contains("校验通过 · 完成", vm.Message);
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

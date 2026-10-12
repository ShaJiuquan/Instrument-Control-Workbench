using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Devices;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class DeviceSettingsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceConfigurationWorkspace workspace;
    private readonly string activeText;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private ValidatedDeviceConfiguration? validated;
    public DeviceSettingsViewModel(DeviceConfigurationWorkspace workspace, ValidatedDeviceConfiguration active, string path)
    {
        this.workspace = workspace; activeText = active.Text; documentPath = path; ActiveSettingsPath = Path.GetFullPath(path); Apply(active); IsDirty = false;
    }
    public string ActiveSettingsPath { get; }
    [ObservableProperty] private string documentText = "";
    [ObservableProperty] private string documentPath;
    [ObservableProperty] private string message = "当前运行使用启动时的配置。保存后需重新启动工作台。";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool restartRequired;
    public bool IsReadOnly => disposed || IsBusy;
    public bool IsValidated => validated is not null;
    partial void OnDocumentTextChanged(string value)
    { validated = null; IsDirty = true; HasError = false; Message = "草稿已修改，请验证后保存；当前设备不受影响。"; Refresh(); }
    partial void OnDocumentPathChanged(string value) => Refresh();
    partial void OnIsBusyChanged(bool value) => Refresh();
    private bool CanEdit() => !disposed && !IsBusy;
    private bool CanAccessFile() => CanEdit() && !string.IsNullOrWhiteSpace(DocumentPath);
    private void Refresh()
    {
        OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(IsValidated));
        ValidateCommand.NotifyCanExecuteChanged(); LoadCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Validate()
    {
        try { Apply(workspace.Validate(DocumentText)); Message = "设备文档有效。驱动支持情况在下次启动时检查。"; }
        catch (Exception ex) { HasError = true; Message = ex.Message; validated = null; Refresh(); }
    }
    [RelayCommand(CanExecute = nameof(CanAccessFile))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try { Apply(await workspace.LoadAsync(DocumentPath, lifetime.Token)); IsDirty = false; Message = "已载入文档；当前设备仍使用启动时配置。"; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"载入失败，草稿保留：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanAccessFile))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var saved = await workspace.SaveAsync(DocumentPath, DocumentText, lifetime.Token);
            Apply(saved); IsDirty = false;
            if (Path.GetFullPath(DocumentPath) == ActiveSettingsPath)
            {
                RestartRequired = saved.Text != activeText;
                Message = RestartRequired ? "已保存；重新启动后使用新配置。当前运行与设备保持原配置。" : "已保存，内容与当前启动配置一致。";
            }
            else Message = "已导出设备文档。启动配置路径仍为：" + ActiveSettingsPath;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"保存失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    private void Apply(ValidatedDeviceConfiguration document)
    { DocumentText = document.Text; validated = document; HasError = false; Refresh(); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); Refresh();
        if (LoadCommand.ExecutionTask is { } loading) await loading;
        if (SaveCommand.ExecutionTask is { } saving) await saving;
        lifetime.Dispose();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Devices;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class DeviceOperatorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceRegistry registry;
    private readonly Func<bool> canOperate;
    private readonly Action changed;
    private readonly CancellationTokenSource lifetime = new();
    private bool closing;
    private bool disposed;
    public DeviceOperatorViewModel(DeviceRegistry registry, Func<bool> canOperate, Action changed)
    {
        this.registry = registry; this.canOperate = canOperate; this.changed = changed;
        foreach (var descriptor in registry.Devices) Devices.Add(new(descriptor));
        selected = Devices.FirstOrDefault();
    }
    public ObservableCollection<DeviceStatusItem> Devices { get; } = new();
    public ObservableCollection<DeviceParameterItem> Parameters { get; } = new();
    [ObservableProperty] private DeviceStatusItem? selected;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string message = "选择设备并连接或刷新；运行中的资源由执行层管理。";
    [ObservableProperty] private bool hasError;
    public bool HasSelection => Selected is not null;
    public bool CanSelect => !IsBusy && !closing && !disposed;
    partial void OnSelectedChanged(DeviceStatusItem? value) { UpdateParameters(); RefreshCommands(); }
    partial void OnIsBusyChanged(bool value) { RefreshCommands(); changed(); }
    private bool CanRefresh() => CanSelect && canOperate() && Selected is not null;
    private bool CanConnect() => CanRefresh() && Selected is { Connected: false };
    private bool CanDisconnect() => CanRefresh() && Selected is { Connected: true };
    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSelect));
        ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (Selected is not { } device) return; IsBusy = true; HasError = false;
        try { device.Apply(await registry.ConnectAsync(device.Descriptor.Id, lifetime.Token)); UpdateParameters(); Message = "已收到设备连接回报。"; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"连接失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (Selected is not { } device) return; IsBusy = true; HasError = false;
        try { await registry.DisconnectAsync(device.Descriptor.Id, lifetime.Token); device.Apply(await registry.GetStatusAsync(device.Descriptor.Id, lifetime.Token)); UpdateParameters(); Message = "设备已断开。"; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"断开失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (Selected is not { } device) return; IsBusy = true; HasError = false;
        try { device.Apply(await registry.GetStatusAsync(device.Descriptor.Id, lifetime.Token)); UpdateParameters(); Message = "已刷新设备状态。"; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"刷新失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    public void ApplyFact(WorkbenchNotification fact)
    {
        if (fact.Device is not { } status) return;
        Devices.FirstOrDefault(d => d.Descriptor.Id == status.Descriptor.Id)?.Apply(status);
        if (Selected?.Descriptor.Id == status.Descriptor.Id) UpdateParameters();
        RefreshCommands();
    }
    private void UpdateParameters()
    {
        Parameters.Clear();
        if (Selected is not { } device) return;
        foreach (var parameter in device.Descriptor.Parameters)
        {
            var value = device.Status is { } status && status.Values.TryGetValue(parameter.Id, out var actual) ? actual.Value.ToString("G9", CultureInfo.InvariantCulture) : "—";
            Parameters.Add(new(parameter.Label, parameter.Id.Value, value, parameter.Unit.ToString(), parameter.Access.ToString()));
        }
    }
    public async Task DisconnectForCloseAsync(CancellationToken token)
    {
        closing = true; RefreshCommands();
        try
        {
            if (ConnectCommand.ExecutionTask is { } connecting) await connecting.WaitAsync(token);
            if (DisconnectCommand.ExecutionTask is { } disconnecting) await disconnecting.WaitAsync(token);
            if (RefreshCommand.ExecutionTask is { } refreshing) await refreshing.WaitAsync(token);
            var errors = new List<Exception>();
            foreach (var device in Devices)
            {
                try
                {
                    device.Apply(await registry.GetStatusAsync(device.Descriptor.Id, token));
                    if (device.Connected)
                    {
                        await registry.DisconnectAsync(device.Descriptor.Id, token);
                        device.Apply(await registry.GetStatusAsync(device.Descriptor.Id, token));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add(ex); }
            }
            UpdateParameters();
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException(errors);
        }
        catch { closing = false; RefreshCommands(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); RefreshCommands();
        if (ConnectCommand.ExecutionTask is { } connecting) await connecting;
        if (DisconnectCommand.ExecutionTask is { } disconnecting) await disconnecting;
        if (RefreshCommand.ExecutionTask is { } refreshing) await refreshing;
        lifetime.Dispose();
    }
}

public sealed record DeviceParameterItem(string Label, string Id, string Value, string Unit, string Access);
public sealed partial class DeviceStatusItem(DeviceDescriptor descriptor) : ObservableObject
{
    public DeviceDescriptor Descriptor { get; } = descriptor;
    public string Id => Descriptor.Id.Value;
    public string Name => Descriptor.DisplayName;
    public string Kind => $"{Descriptor.Model} · {Descriptor.DriverKind}";
    public DeviceStatus? Status { get; private set; }
    [ObservableProperty] private bool connected;
    [ObservableProperty] private string lifecycle = "尚未读取";
    [ObservableProperty] private string safety = "停止状态未知";
    [ObservableProperty] private string updated = "—";
    public void Apply(DeviceStatus status)
    {
        if (status.Descriptor.Id != Descriptor.Id) return;
        Status = status; Connected = status.Connected; Lifecycle = status.Lifecycle.ToString();
        Safety = !status.SafetyConfirmed ? "停止状态未确认" : Descriptor.StopPolicy switch
        {
            DeviceStopPolicy.OutputOff => "已回报输出关闭",
            DeviceStopPolicy.HoldCurrentPosition => "已回报保持当前位置",
            _ => "路由清理已确认",
        };
        Updated = status.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}

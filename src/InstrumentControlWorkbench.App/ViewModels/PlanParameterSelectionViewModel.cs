using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanDeviceChoice(DeviceDescriptor Descriptor)
{
    public string Label => $"{Descriptor.DisplayName} · {Descriptor.Id} · {Descriptor.DriverKind}";
}
public sealed record PlanParameterChoice(ParameterDescriptor Descriptor)
{
    public string Label => $"{Descriptor.Label} · {Descriptor.Id}";
}

public partial class PlanParameterSelectionViewModel : ObservableObject
{
    private readonly Action changed;
    private readonly ParameterAccess access;
    private bool loading;
    public PlanParameterSelectionViewModel(IReadOnlyList<DeviceDescriptor> devices, ParameterAccess access,
        ParameterAddress? address, Action changed, bool outputOnly = false)
    {
        this.changed = changed; this.access = access;
        Devices = devices.Where(d => outputOnly ? d.StopPolicy == DeviceStopPolicy.OutputOff : d.Parameters.Any(p => p.Access.HasFlag(access)))
            .Select(d => new PlanDeviceChoice(d)).ToArray();
        loading = true;
        SelectedDevice = Devices.FirstOrDefault(d => d.Descriptor.Id == address?.Device);
        SelectedParameter = Parameters.FirstOrDefault(p => p.Descriptor.Id == address?.Parameter);
        loading = false;
    }
    public IReadOnlyList<PlanDeviceChoice> Devices { get; }
    [ObservableProperty] private PlanDeviceChoice? selectedDevice;
    [ObservableProperty] private PlanParameterChoice? selectedParameter;
    [ObservableProperty] private IReadOnlyList<PlanParameterChoice> parameters = Array.Empty<PlanParameterChoice>();
    [ObservableProperty] private bool isReadOnly;
    public string Constraint => ActualParameter() is { } p ? $"{p.Unit} · {p.Access} · 声明范围 {Bound(p.Minimum)} … {Bound(p.Maximum)}" : "请选择设备和参数；单位由设备描述确定。";
    private static string Bound(Quantity? quantity) => quantity is { } q ? q.Value.ToString("G17", CultureInfo.InvariantCulture) : "未声明";
    partial void OnSelectedDeviceChanged(PlanDeviceChoice? value)
    {
        // Resolve from our immutable catalog, not from a caller-supplied descriptor.
        var device = Devices.FirstOrDefault(d => d.Descriptor.Id == value?.Descriptor.Id);
        Parameters = device?.Descriptor.Parameters.Where(p => p.Access.HasFlag(access)).Select(p => new PlanParameterChoice(p)).ToArray() ?? Array.Empty<PlanParameterChoice>();
        SelectedParameter = null;
        OnPropertyChanged(nameof(Constraint)); if (!loading) changed();
    }
    partial void OnSelectedParameterChanged(PlanParameterChoice? value) { OnPropertyChanged(nameof(Constraint)); if (!loading) changed(); }
    public DeviceDescriptor ResolveDevice() => Devices.FirstOrDefault(d => d.Descriptor.Id == SelectedDevice?.Descriptor.Id)?.Descriptor
        ?? throw new InvalidOperationException("请选择已注册的设备。");
    private ParameterDescriptor? ActualParameter() => Devices.FirstOrDefault(d => d.Descriptor.Id == SelectedDevice?.Descriptor.Id)?.Descriptor.Parameters
        .FirstOrDefault(p => p.Id == SelectedParameter?.Descriptor.Id && p.Access.HasFlag(access));
    public ParameterDescriptor ResolveParameter() => ActualParameter()
        ?? throw new InvalidOperationException("请选择具有所需读写权限的设备参数。");
    public ParameterAddress BuildAddress() => new(ResolveDevice().Id, ResolveParameter().Id);
}

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanSr830FrameFormViewModel : ObservableObject
{
    private readonly ReadSr830FrameStep original;
    private readonly Action changed;
    private bool loading = true;
    public PlanSr830FrameFormViewModel(ReadSr830FrameStep step, IReadOnlyList<DeviceDescriptor> devices, Action changed, bool template = false)
    {
        original = step; this.changed = changed;
        Devices = Array.AsReadOnly(devices.Where(d => d.Model == "SR830" && d.StopPolicy == DeviceStopPolicy.AbortAcquisition &&
            (d.Parameters.SequenceEqual(Sr830Parameters.All) || d.Parameters.SequenceEqual(Sr830Parameters.WithReference)))
            .Select(d => new PlanDeviceChoice(d)).ToArray());
        selectedDevice = template ? null : Devices.SingleOrDefault(d => d.Descriptor.Id == step.Device);
        selectedInput = template ? null : step.Input; prefix = template ? "" : step.Prefix.Value; includeReference = step.IncludeReference;
        loading = false;
    }
    public IReadOnlyList<PlanDeviceChoice> Devices { get; }
    public Sr830InputMode[] Inputs { get; } = Enum.GetValues<Sr830InputMode>();
    private PlanDeviceChoice? RegisteredDevice => Devices.SingleOrDefault(d => d.Descriptor.Id == SelectedDevice?.Descriptor.Id);
    public bool ReferenceAvailable => RegisteredDevice?.Descriptor.Parameters.SequenceEqual(Sr830Parameters.WithReference) == true;
    public bool CanChooseReference => !IsReadOnly && (ReferenceAvailable || IncludeReference);
    public string ReferenceSummary => ReferenceAvailable ? "可记录参考源类型、频率、振幅和谐波。" : "该设备未声明参考读回能力；已保存的选项会保留，应用前须选择兼容设备。";
    public string InputSummary => SelectedInput switch
    {
        Sr830InputMode.Current1M => "原始 X/Y/R：V · 名义跨阻 1 MΩ，换算结果：A。",
        Sr830InputMode.Current100M => "原始 X/Y/R：V · 名义跨阻 100 MΩ，换算结果：A。",
        Sr830InputMode.VoltageA or Sr830InputMode.VoltageAB => "原始 X/Y/R 与名义输入结果：V；相位：度。",
        _ => "请选择预期输入模式；运行时须与实际输入设置一致。",
    };
    [ObservableProperty] private PlanDeviceChoice? selectedDevice;
    [ObservableProperty] private Sr830InputMode? selectedInput;
    [ObservableProperty] private string prefix;
    [ObservableProperty] private bool includeReference;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedDeviceChanged(PlanDeviceChoice? value) { Refresh(); MarkDirty(); }
    partial void OnSelectedInputChanged(Sr830InputMode? value) { OnPropertyChanged(nameof(InputSummary)); MarkDirty(); }
    partial void OnPrefixChanged(string value) => MarkDirty();
    partial void OnIncludeReferenceChanged(bool value) { OnPropertyChanged(nameof(CanChooseReference)); MarkDirty(); }
    partial void OnIsReadOnlyChanged(bool value) => Refresh();
    private void Refresh() { OnPropertyChanged(nameof(ReferenceAvailable)); OnPropertyChanged(nameof(CanChooseReference)); OnPropertyChanged(nameof(ReferenceSummary)); }
    private void MarkDirty() { if (!loading) changed(); }
    public ReadSr830FrameStep Build()
    {
        var device = RegisteredDevice ?? throw new InvalidOperationException("请选择已注册、支持原始帧采集的 SR830。");
        var input = SelectedInput ?? throw new InvalidOperationException("请选择预期 SR830 输入模式。");
        if (!Enum.IsDefined(input)) throw new ArgumentException("SR830 输入模式无效。");
        if (IncludeReference && !ReferenceAvailable) throw new InvalidOperationException("所选 SR830 不支持参考读回；请选择兼容设备或明确取消该选项。");
        return new(original.Id, device.Descriptor.Id, input, new(Prefix), IncludeReference);
    }
}

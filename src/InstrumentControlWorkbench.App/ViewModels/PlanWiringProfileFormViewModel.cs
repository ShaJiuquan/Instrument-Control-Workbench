using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWiringProfileFormViewModel : ObservableObject
{
    private readonly InstrumentWiringProfile? original;
    private readonly Action changed;
    private bool loading = true;
    private bool edited;
    public PlanWiringProfileFormViewModel(InstrumentWiringProfile? wiring, IReadOnlyList<DeviceDescriptor> devices, Action changed)
    {
        original = wiring; this.changed = changed; idText = wiring?.Id.Value ?? ""; description = wiring?.Label ?? "";
        Sources = Array.AsReadOnly(devices.Where(device => device.StopPolicy == DeviceStopPolicy.OutputOff && device.Model is "K2450" or "K6221").Select(device => new PlanDeviceChoice(device)).ToArray());
        Meters = Array.AsReadOnly(devices.Where(device => device.StopPolicy == DeviceStopPolicy.AbortAcquisition && device.Model == "K2182").Select(device => new PlanDeviceChoice(device)).ToArray());
        SelectedSource = Sources.FirstOrDefault(choice => choice.Descriptor.Id == wiring?.SourceMeter); UseIndependentMeter = wiring?.VoltageMeter is not null; SelectedMeter = Meters.FirstOrDefault(choice => choice.Descriptor.Id == wiring?.VoltageMeter);
        var labels = new[] { "源正端", "源负端", "测量正端", "测量负端" }; var originalPorts = wiring is null ? null : new[] { wiring.SourcePlus, wiring.SourceMinus, wiring.SensePlus, wiring.SenseMinus };
        Ports = Array.AsReadOnly(labels.Select((label, index) => { var port = new PlanMatrixPortFormViewModel(label, Changed); if (originalPorts is not null) { port.SlotText = originalPorts[index].Slot.ToString(CultureInfo.InvariantCulture); port.RowText = originalPorts[index].Row.ToString(CultureInfo.InvariantCulture); } return port; }).ToArray()); loading = false;
    }
    public IReadOnlyList<PlanDeviceChoice> Sources { get; }
    public IReadOnlyList<PlanDeviceChoice> Meters { get; }
    public IReadOnlyList<PlanMatrixPortFormViewModel> Ports { get; }
    public string Label => (original?.Id.Value ?? IdText) + " · " + Description;
    public bool IsIdentityReadOnly => IsReadOnly || original is not null;
    public bool CanChooseMeter => UseIndependentMeter && !IsReadOnly;
    [ObservableProperty] private string idText;
    [ObservableProperty] private string description;
    [ObservableProperty] private PlanDeviceChoice? selectedSource;
    [ObservableProperty] private PlanDeviceChoice? selectedMeter;
    [ObservableProperty] private bool useIndependentMeter;
    [ObservableProperty] private bool isReadOnly;
    partial void OnIdTextChanged(string value) { OnPropertyChanged(nameof(Label)); Changed(); }
    partial void OnDescriptionChanged(string value) { OnPropertyChanged(nameof(Label)); Changed(); }
    partial void OnSelectedSourceChanged(PlanDeviceChoice? value) { if (!loading) foreach (var port in Ports) port.Clear(); Changed(); }
    partial void OnSelectedMeterChanged(PlanDeviceChoice? value) { if (!loading) { Ports[2].Clear(); Ports[3].Clear(); } Changed(); }
    partial void OnUseIndependentMeterChanged(bool value) { if (!loading) foreach (var port in Ports) port.Clear(); OnPropertyChanged(nameof(CanChooseMeter)); Changed(); }
    partial void OnIsReadOnlyChanged(bool value) { OnPropertyChanged(nameof(IsIdentityReadOnly)); OnPropertyChanged(nameof(CanChooseMeter)); foreach (var port in Ports) port.IsReadOnly = value; }
    private void Changed() { if (!loading) { edited = true; changed(); } }
    public InstrumentWiringProfile Build()
    {
        if (!edited && original is not null) return original;
        var source = Sources.FirstOrDefault(choice => choice.Descriptor.Id == SelectedSource?.Descriptor.Id)?.Descriptor ?? throw new InvalidOperationException("请选择已注册的支持源。");
        var meter = UseIndependentMeter ? Meters.FirstOrDefault(choice => choice.Descriptor.Id == SelectedMeter?.Descriptor.Id)?.Descriptor ?? throw new InvalidOperationException("请选择已注册 K2182 电压表。") : null;
        if (source.Model == "K6221" && meter is null) throw new InvalidOperationException("K6221 需要独立电压表。");
        return new(original?.Id ?? new(IdText), source.Id, Ports[0].Build(), Ports[1].Build(), Ports[2].Build(), Ports[3].Build(), Description, meter?.Id);
    }
}

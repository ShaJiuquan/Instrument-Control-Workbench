using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PpmsWiringImportViewModel : ObservableObject
{
    private readonly Action<PpmsWiringImportViewModel> changed;
    public PpmsWiringImportViewModel(MatrixWiringLayout layout, IReadOnlyList<PlanDeviceChoice> sources,
        IReadOnlyList<PlanDeviceChoice> meters, Action<PpmsWiringImportViewModel> changed)
    {
        Layout = layout; Sources = sources; Meters = meters; this.changed = changed;
    }
    public MatrixWiringLayout Layout { get; }
    public string Label => Layout.Id.Value + " / " + Layout.Label;
    public string Ports => $"源 + {Layout.SourcePlus.Slot}:{Layout.SourcePlus.Row} / − {Layout.SourceMinus.Slot}:{Layout.SourceMinus.Row}；测量 + {Layout.SensePlus.Slot}:{Layout.SensePlus.Row} / − {Layout.SenseMinus.Slot}:{Layout.SenseMinus.Row}";
    public IReadOnlyList<PlanDeviceChoice> Sources { get; }
    public IReadOnlyList<PlanDeviceChoice> Meters { get; }
    [ObservableProperty] private bool isIncluded;
    [ObservableProperty] private bool isReadOnly;
    [ObservableProperty] private PlanDeviceChoice? source;
    [ObservableProperty] private PlanDeviceChoice? voltageMeter;
    public bool CanEditBinding => !IsReadOnly && IsIncluded;
    partial void OnIsIncludedChanged(bool value) { Refresh(); changed(this); }
    partial void OnIsReadOnlyChanged(bool value) => Refresh();
    partial void OnSourceChanged(PlanDeviceChoice? value) { VoltageMeter = null; changed(this); }
    partial void OnVoltageMeterChanged(PlanDeviceChoice? value) => changed(this);
    private void Refresh() { OnPropertyChanged(nameof(CanEditBinding)); UseIntegratedMeterCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanEditBinding))]
    private void UseIntegratedMeter() { if (CanEditBinding) VoltageMeter = null; }
    public ImportWiringSelection Capture()
    {
        var source = Sources.FirstOrDefault(s => s.Descriptor.Id == Source?.Descriptor.Id) ?? throw new InvalidOperationException(Label + "：请选择已注册的源设备。");
        var meter = VoltageMeter is null ? null : Meters.FirstOrDefault(m => m.Descriptor.Id == VoltageMeter.Descriptor.Id)
            ?? throw new InvalidOperationException(Label + "：请选择已注册的电压表。");
        return new(Layout.Id, source.Descriptor.Id, meter?.Descriptor.Id);
    }
}

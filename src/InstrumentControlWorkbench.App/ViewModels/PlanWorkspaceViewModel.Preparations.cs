using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Devices;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PreparationDeviceChoice(DeviceDescriptor Descriptor)
{
    public string Label => $"{Descriptor.DisplayName} · {Descriptor.Id} · {Descriptor.DriverKind}";
}

public partial class PlanWorkspaceViewModel
{
    private bool loadingPreparation;
    [ObservableProperty] private PreparationDeviceChoice? selectedPreparationDevice;
    [ObservableProperty] private SourcePreparationFormViewModel? preparationForm;
    [ObservableProperty] private bool preparationDirty;
    public IReadOnlyList<PreparationDeviceChoice> PreparationDevices => workspace.Devices
        .Where(d => d.Model is "K2450" or "K6221").Select(d => new PreparationDeviceChoice(d)).ToArray();
    public bool HasUnappliedEdits => NodeFragmentDirty || PreparationDirty;
    public bool HasPreparationForm => PreparationForm is not null;
    public bool CanSelectPreparation => CanEdit && validated is not null && !HasUnappliedEdits;
    public bool CanEditPreparation => CanEdit && validated is not null && !NodeFragmentDirty;
    private bool CanApplyPreparation() => CanEditPreparation && PreparationDirty && PreparationForm is not null;
    private bool CanDiscardPreparation() => CanEdit && validated is not null && PreparationDirty;
    private bool CanRemovePreparation() => CanEditPreparation && !PreparationDirty && PreparationForm?.HasExistingPreparation == true;

    partial void OnSelectedPreparationDeviceChanged(PreparationDeviceChoice? oldValue, PreparationDeviceChoice? newValue)
    {
        if (loadingPreparation) return;
        if (!CanSelectPreparation || newValue is not null && !PreparationDevices.Contains(newValue))
        {
            loadingPreparation = true;
            try { SelectedPreparationDevice = oldValue; }
            finally { loadingPreparation = false; }
            return;
        }
        ReloadPreparationEditor(); RefreshCommands();
    }
    partial void OnPreparationFormChanged(SourcePreparationFormViewModel? value) => OnPropertyChanged(nameof(HasPreparationForm));
    partial void OnPreparationDirtyChanged(bool value) { RefreshCommands(); changed(); }
    private void RefreshPreparationCommands()
    {
        OnPropertyChanged(nameof(HasUnappliedEdits)); OnPropertyChanged(nameof(CanSelectPreparation)); OnPropertyChanged(nameof(CanEditPreparation));
        ApplyPreparationCommand.NotifyCanExecuteChanged(); DiscardPreparationCommand.NotifyCanExecuteChanged(); RemovePreparationCommand.NotifyCanExecuteChanged();
        if (PreparationForm is { } form) form.IsReadOnly = !CanEditPreparation;
    }
    private void ClearPreparationEditor()
    {
        loadingPreparation = true;
        try { PreparationForm = null; PreparationDirty = false; }
        finally { loadingPreparation = false; }
    }
    private void ReloadPreparationEditor()
    {
        loadingPreparation = true;
        try
        {
            PreparationForm = null; PreparationDirty = false;
            if (validated is null) return;
            var selected = PreparationDevices.FirstOrDefault(d => d.Descriptor.Id == SelectedPreparationDevice?.Descriptor.Id) ?? PreparationDevices.FirstOrDefault();
            SelectedPreparationDevice = selected;
            if (selected is null) return;
            var source = validated.Plan.Preparations.SingleOrDefault(p => p.Device == selected.Descriptor.Id);
            var current = validated.Plan.CurrentSourcePreparations.SingleOrDefault(p => p.Device == selected.Descriptor.Id);
            SourcePreparationFormViewModel? form = null;
            form = new(selected.Descriptor, source, current, () =>
            {
                if (loadingPreparation || !ReferenceEquals(PreparationForm, form)) return;
                PreparationDirty = true; HasError = false; Message = "设备准备修改尚未应用；完整验证或撤销后才能运行。";
                RefreshCommands(); changed();
            });
            PreparationForm = form;
        }
        finally { loadingPreparation = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApplyPreparation))]
    private void ApplyPreparation()
    {
        if (!CanApplyPreparation()) return;
        try
        {
            var form = PreparationForm!;
            var document = form.IsCurrentSource ? workspace.UpdateCurrentSourcePreparation(validated!.Plan, form.BuildCurrentSource())
                : workspace.UpdateSourcePreparation(validated!.Plan, form.BuildSourceMeter());
            Apply(document); IsDirty = true; Message = "设备准备已通过完整计划验证；运行时由执行层配置。";
        }
        catch (Exception error) { HasError = true; Message = $"设备准备未应用，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
    }
    [RelayCommand(CanExecute = nameof(CanDiscardPreparation))]
    private void DiscardPreparation()
    {
        if (!CanDiscardPreparation()) return;
        ReloadPreparationEditor(); HasError = false; Message = "设备准备修改已撤销。"; RefreshCommands(); changed();
    }
    [RelayCommand(CanExecute = nameof(CanRemovePreparation))]
    private void RemovePreparation()
    {
        if (!CanRemovePreparation()) return;
        try { Apply(workspace.RemovePreparation(validated!.Plan, PreparationForm!.Device.Id)); IsDirty = true; Message = "设备准备已移除，并通过完整计划验证。"; }
        catch (Exception error) { HasError = true; Message = $"设备准备未移除，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
    }
}

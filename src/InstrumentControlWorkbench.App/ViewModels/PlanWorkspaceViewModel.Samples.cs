using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel
{
    private bool loadingSample;
    [ObservableProperty] private PlanSampleChoice? selectedSample;
    [ObservableProperty] private PlanSampleDetailsFormViewModel? sampleForm;
    [ObservableProperty] private bool sampleDirty;
    public IReadOnlyList<PlanSampleChoice> SampleChoices => Array.AsReadOnly((validated?.Plan.Routing?.Samples ?? []).Select(sample => new PlanSampleChoice(sample)).ToArray());
    public bool HasSampleForm => SampleForm is not null;
    public bool CanSelectSample => CanEdit && validated is not null && !HasUnappliedEdits;
    public bool CanEditSample => CanEdit && validated is not null && !NodeFragmentDirty && !PreparationDirty;
    private bool CanApplySample() => CanEditSample && SampleDirty && SampleForm is not null;
    private bool CanDiscardSample() => CanEdit && validated is not null && SampleDirty;
    partial void OnSelectedSampleChanged(PlanSampleChoice? oldValue, PlanSampleChoice? newValue)
    {
        if (loadingSample) return;
        if (!CanSelectSample || newValue is not null && !SampleChoices.Contains(newValue))
        {
            loadingSample = true; try { SelectedSample = oldValue; } finally { loadingSample = false; }
            return;
        }
        ReloadSampleEditor(); RefreshCommands();
    }
    partial void OnSampleFormChanged(PlanSampleDetailsFormViewModel? value) => OnPropertyChanged(nameof(HasSampleForm));
    partial void OnSampleDirtyChanged(bool value) { RefreshCommands(); changed(); }
    private void RefreshSampleCommands()
    {
        OnPropertyChanged(nameof(HasUnappliedEdits)); OnPropertyChanged(nameof(CanSelectSample)); OnPropertyChanged(nameof(CanEditSample));
        ApplySampleCommand.NotifyCanExecuteChanged(); DiscardSampleCommand.NotifyCanExecuteChanged();
        if (SampleForm is { } form) form.IsReadOnly = !CanEditSample;
    }
    private void ClearSampleEditor()
    {
        loadingSample = true; try { SampleForm = null; SampleDirty = false; } finally { loadingSample = false; }
        OnPropertyChanged(nameof(SampleChoices));
    }
    private void ReloadSampleEditor()
    {
        loadingSample = true;
        try
        {
            SampleForm = null; SampleDirty = false; OnPropertyChanged(nameof(SampleChoices));
            if (validated is null) return;
            var selected = SampleChoices.FirstOrDefault(sample => sample.Profile.Id == SelectedSample?.Profile.Id) ?? SampleChoices.FirstOrDefault();
            SelectedSample = selected; if (selected is null) return;
            PlanSampleDetailsFormViewModel? form = null;
            form = new(selected.Profile, () =>
            {
                if (loadingSample || !ReferenceEquals(SampleForm, form)) return;
                SampleDirty = true; HasError = false; Message = "样品资料修改尚未应用；完整验证或撤销后才能运行。"; RefreshCommands(); changed();
            });
            SampleForm = form;
        }
        finally { loadingSample = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApplySample))]
    private void ApplySample()
    {
        if (!CanApplySample()) return;
        try { Apply(workspace.UpdateSampleDetails(validated!.Plan, SampleForm!.Id, SampleForm.Build())); IsDirty = true; Message = "样品资料已通过完整计划验证；下次运行使用新几何。"; }
        catch (Exception error) { HasError = true; Message = $"样品资料未应用，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
    }
    [RelayCommand(CanExecute = nameof(CanDiscardSample))]
    private void DiscardSample()
    {
        if (!CanDiscardSample()) return;
        ReloadSampleEditor(); HasError = false; Message = "样品资料修改已撤销。"; RefreshCommands(); changed();
    }
}

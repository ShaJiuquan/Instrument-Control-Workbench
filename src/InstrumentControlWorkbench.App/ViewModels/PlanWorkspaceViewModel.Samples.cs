using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel
{
    private bool loadingSample;
    private bool mappingEdited;
    [ObservableProperty] private bool creatingSample;
    [ObservableProperty] private PlanSampleCreationFormViewModel? creationForm;
    [ObservableProperty] private PlanSampleElectrodesFormViewModel? electrodeForm;
    [ObservableProperty] private PlanSampleChoice? selectedSample;
    [ObservableProperty] private PlanSampleDetailsFormViewModel? sampleForm;
    [ObservableProperty] private bool sampleDirty;
    public IReadOnlyList<PlanSampleChoice> SampleChoices => Array.AsReadOnly((validated?.Plan.Routing?.Samples ?? []).Select(sample => new PlanSampleChoice(sample)).ToArray());
    public bool HasSampleForm => SampleForm is not null;
    public bool CanSelectSample => CanEdit && validated is not null && !HasUnappliedEdits;
    public bool CanEditSample => CanEdit && validated is not null && !NodeFragmentDirty && !PreparationDirty;
    private bool CanApplySample() => CanEditSample && SampleDirty && (CreatingSample ? CreationForm is not null : SampleForm is not null);
    private bool CanDiscardSample() => CanEdit && validated is not null && SampleDirty;
    public bool CanCreateSample => CanSelectSample && validated?.Plan.Routing is { Samples.Count: < 64 };
    private bool CanRemoveSample() => CanSelectSample && SampleForm is not null && validated?.Plan.Routing is { Samples.Count: > 1 };
    partial void OnCreatingSampleChanged(bool value) => OnPropertyChanged(nameof(IsExistingSample));
    public bool IsExistingSample => !CreatingSample;
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
        OnPropertyChanged(nameof(CanCreateSample)); CreateSampleCommand.NotifyCanExecuteChanged(); RemoveSampleCommand.NotifyCanExecuteChanged();
        ApplySampleCommand.NotifyCanExecuteChanged(); DiscardSampleCommand.NotifyCanExecuteChanged();
        if (SampleForm is { } form) form.IsReadOnly = !CanEditSample || CreatingSample;
        if (ElectrodeForm is { } mapping) mapping.IsReadOnly = !CanEditSample || CreatingSample;
        if (CreationForm is { } creation) creation.IsReadOnly = !CanEditSample;
    }
    private void ClearSampleEditor()
    {
        loadingSample = true; try { SampleForm = null; ElectrodeForm = null; CreationForm = null; CreatingSample = false; mappingEdited = false; SampleDirty = false; } finally { loadingSample = false; }
        OnPropertyChanged(nameof(SampleChoices));
    }
    private void ReloadSampleEditor()
    {
        loadingSample = true;
        try
        {
            SampleForm = null; ElectrodeForm = null; CreationForm = null; CreatingSample = false; mappingEdited = false; SampleDirty = false; OnPropertyChanged(nameof(SampleChoices));
            if (validated is null) return;
            var selected = SampleChoices.FirstOrDefault(sample => sample.Profile.Id == SelectedSample?.Profile.Id) ?? SampleChoices.FirstOrDefault();
            SelectedSample = selected; if (selected is null) return;
            PlanSampleDetailsFormViewModel? form = null;
            form = new(selected.Profile, () =>
            {
                if (loadingSample || CreatingSample || !ReferenceEquals(SampleForm, form)) return;
                SampleDirty = true; HasError = false; Message = "样品资料修改尚未应用；完整验证或撤销后才能运行。"; RefreshCommands(); changed();
            });
            SampleForm = form;
            if (validated.Plan.Routing is { } routing)
            {
                PlanSampleElectrodesFormViewModel? mapping = null;
                mapping = new(routing, selected.Profile.Electrodes, selected.Profile.Topology, () =>
                {
                    if (loadingSample || CreatingSample || !ReferenceEquals(ElectrodeForm, mapping)) return;
                    mappingEdited = true; SampleDirty = true; HasError = false; Message = "电极映射修改尚未应用；选择验证接线并校验整个计划。"; RefreshCommands(); changed();
                }); ElectrodeForm = mapping;
            }
        }
        finally { loadingSample = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApplySample))]
    private void ApplySample()
    {
        if (!CanApplySample()) return;
        try
        {
            if (CreatingSample)
            {
                var created = CreationForm!.Build(); var document = workspace.AddSample(validated!.Plan, created, CreationForm.Mapping!.BuildWiring());
                Apply(document); SelectedSample = SampleChoices.Single(choice => choice.Profile.Id == created.Id);
            }
            else
            {
                var document = mappingEdited ? workspace.UpdateSampleDefinition(validated!.Plan, SampleForm!.Id, SampleForm.Build(), ElectrodeForm!.Build(), ElectrodeForm.BuildWiring()) : workspace.UpdateSampleDetails(validated!.Plan, SampleForm!.Id, SampleForm.Build());
                Apply(document);
            }
            IsDirty = true; Message = "样品已通过路由与完整计划验证。";
        }
        catch (Exception error) { HasError = true; Message = $"样品资料未应用，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
    }
    [RelayCommand(CanExecute = nameof(CanDiscardSample))]
    private void DiscardSample()
    {
        if (!CanDiscardSample()) return;
        ReloadSampleEditor(); HasError = false; Message = "样品资料修改已撤销。"; RefreshCommands(); changed();
    }
    [RelayCommand(CanExecute = nameof(CanCreateSample))]
    private void CreateSample()
    {
        if (!CanCreateSample) return;
        loadingSample = true;
        try
        {
            PlanSampleCreationFormViewModel? form = null;
            form = new(validated!.Plan.Routing!, () =>
            {
                if (loadingSample || !ReferenceEquals(CreationForm, form)) return;
                SampleDirty = true; HasError = false; Message = "新样品草稿尚未应用；请填写标识、资料和真实电极映射。"; RefreshCommands(); changed();
            }); CreationForm = form; CreatingSample = true; SampleDirty = true;
        }
        finally { loadingSample = false; }
        Message = "新样品草稿尚未应用；请填写标识、资料和真实电极映射。"; RefreshCommands(); changed();
    }
    [RelayCommand(CanExecute = nameof(CanRemoveSample))]
    private void RemoveSample()
    {
        if (!CanRemoveSample()) return;
        try { Apply(workspace.RemoveSample(validated!.Plan, SampleForm!.Id)); IsDirty = true; Message = "未使用样品已移除；原运行快照保持不变。"; }
        catch (Exception error) { HasError = true; Message = $"样品未移除，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
    }

}

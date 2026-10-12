using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Measurements;
using InstrumentControlWorkbench.Core.Data;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class MainWindowViewModel
{
    public PlanWorkspaceViewModel? Plans { get; }
    public bool IsPlanPage => Page == WorkbenchPage.Plans;
    public bool HasPlanWorkspace => Plans is not null;
    [ObservableProperty] private bool pauseRequested;
    [ObservableProperty] private bool isPlanRun;

    public bool IsIvDataPage => IsDataPage && !IsPlanRun;
    public bool IsPlanDataPage => IsDataPage && IsPlanRun;
    partial void OnIsPlanRunChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIvDataPage)); OnPropertyChanged(nameof(IsPlanDataPage));
    }
    [RelayCommand] private void OpenPlans() => Page = WorkbenchPage.Plans;
    private bool CanStartPlan() => !closing && CanEdit && Plans is { IsValidated: true };
    private bool CanPauseRun() => IsBusy && !PauseRequested && runService.Phase is RunExecutionPhase.Starting or RunExecutionPhase.Running;
    private bool CanResumeRun() => IsBusy && runService.Phase == RunExecutionPhase.Paused;
    private bool CanRecoverSafety() => !closing && !IsBusy && !IsConnecting && runService.Phase == RunExecutionPhase.SafetyRecoveryRequired;
    public bool NeedsSafetyRecovery => runService.Phase == RunExecutionPhase.SafetyRecoveryRequired;

    [RelayCommand(CanExecute = nameof(CanStartPlan))]
    private async Task StartPlanAsync()
    {
        if (!CanStartPlan() || Plans?.ValidatedPlan is not { } plan) return;
        IsBusy = true; IsPlanRun = true; PauseRequested = false;
        OperationError = ""; RunState = OperatorRunState.Starting;
        Plans.BeginRun();
        try { await runService.StartPlanAsync(plan, lifetime.Token, new RunMetadata(sampleName: SampleName.Trim())); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { OperationError = $"计划运行失败：{ex.Message}"; RunState = OperatorRunState.Faulted; }
        finally { IsBusy = false; NotifyCommandState(); }
    }
    [RelayCommand(CanExecute = nameof(CanPauseRun))]
    private void PauseRun()
    {
        if (!CanPauseRun()) { NotifyCommandState(); return; }
        try { runService.Pause(); PauseRequested = true; RunState = OperatorRunState.Pausing; }
        catch (InvalidOperationException ex) { OperationError = $"暂停请求未受理：{ex.Message}"; }
        NotifyCommandState();
    }
    [RelayCommand(CanExecute = nameof(CanResumeRun))]
    private void ResumeRun()
    {
        if (!CanResumeRun()) { NotifyCommandState(); return; }
        runService.Resume(); PauseRequested = false; NotifyCommandState();
    }
    [RelayCommand(CanExecute = nameof(CanRecoverSafety))]
    private async Task RecoverSafetyAsync()
    {
        IsBusy = true; OperationError = ""; RunState = OperatorRunState.Recovering;
        try { await runService.RecoverSafetyAsync(lifetime.Token, reconnect: true); RunState = OperatorRunState.Ready; }
        catch (Exception ex) { OperationError = $"安全恢复未完成，资源仍保留：{ex.Message}"; RunState = OperatorRunState.Faulted; }
        finally { IsBusy = false; NotifyCommandState(); }
    }
    private void NotifyPlanCommands()
    {
        OnPropertyChanged(nameof(NeedsSafetyRecovery));
        OnPropertyChanged(nameof(SafetyText));
        Plans?.RefreshCommands();
        StartPlanCommand.NotifyCanExecuteChanged(); PauseRunCommand.NotifyCanExecuteChanged();
        ResumeRunCommand.NotifyCanExecuteChanged(); RecoverSafetyCommand.NotifyCanExecuteChanged();
    }
}

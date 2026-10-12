using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel
{
    private RoutingProfileCatalog? importedProfiles;
    private bool profilesReady;
    private CancellationTokenSource? ppmsOperation;
    private ValidatedPlanDocument? importBackup;
    private bool importBackupDirty;
    private int profilePathVersion;
    private int importOptionsVersion;
    public bool SupportsPpmsImport => workspace.SupportsConfiguredImport;
    public bool SupportsProfileImport => workspace.SupportsRoutingProfileImport;
    public IReadOnlyList<string> ImportAliases => SupportsPpmsImport ? workspace.ImportAliases.Select(a => $"{a.Key} → {a.Value}").ToArray() : Array.Empty<string>();
    public IReadOnlyList<PlanDeviceChoice> ImportMatrices => workspace.ImportDevices.Where(d => d.Category == ConfiguredDeviceCategory.Matrix).Select(d => new PlanDeviceChoice(d.Descriptor)).ToArray();
    public ObservableCollection<PpmsWiringImportViewModel> ImportWirings { get; } = new();
    public IReadOnlyList<PpmsWiringImportViewModel> ChannelWirings => ImportWirings.Where(w => w.IsIncluded && w.Source is not null).ToArray();
    [ObservableProperty] private string ppmsPath = "";
    [ObservableProperty] private bool useRoutingProfiles;
    [ObservableProperty] private string channelsPath = "";
    [ObservableProperty] private string samplesPath = "";
    [ObservableProperty] private string instrumentPortsPath = "";
    [ObservableProperty] private string routeSettleSeconds = "0";
    [ObservableProperty] private string profileSummary = "尚未载入路由档案";
    [ObservableProperty] private PlanDeviceChoice? importMatrix;
    [ObservableProperty] private PpmsWiringImportViewModel? channelWiring;
    public string ImportPreparationSummary => validated is null ? "需先验证当前计划。" :
        "使用当前计划的源准备：" + string.Join("；", validated.Plan.Preparations.Select(p => $"{p.Device} · {p.Function} · {p.Level.Value.ToString("G6", CultureInfo.InvariantCulture)} {p.Level.Unit} · 限值 {p.Compliance.Value.ToString("G6", CultureInfo.InvariantCulture)} {p.Compliance.Unit}")
            .Concat(validated.Plan.CurrentSourcePreparations.Select(p => $"{p.Device} · {p.Settings.Current.Value.ToString("G6", CultureInfo.InvariantCulture)} {p.Settings.Current.Unit} · 限值 {p.Settings.Compliance.Value.ToString("G6", CultureInfo.InvariantCulture)} {p.Settings.Compliance.Unit} · 纯电流源"))) + $"；执行预算 {validated.Plan.MaximumStepExecutions}";
    partial void OnPpmsPathChanged(string value) => ImportOptionsChanged();
    partial void OnUseRoutingProfilesChanged(bool value) => ImportOptionsChanged();
    partial void OnRouteSettleSecondsChanged(string value) => ImportOptionsChanged();
    partial void OnImportMatrixChanged(PlanDeviceChoice? value) => ImportOptionsChanged();
    partial void OnChannelWiringChanged(PpmsWiringImportViewModel? value) => ImportOptionsChanged();
    partial void OnChannelsPathChanged(string value) => ProfilePathsChanged();
    partial void OnSamplesPathChanged(string value) => ProfilePathsChanged();
    partial void OnInstrumentPortsPathChanged(string value) => ProfilePathsChanged();
    private void ProfilePathsChanged()
    {
        profilePathVersion++; importedProfiles = null; profilesReady = false; ImportWirings.Clear(); ChannelWiring = null;
        ProfileSummary = "档案路径已修改，请重新载入。"; OnPropertyChanged(nameof(ChannelWirings)); ImportOptionsChanged();
    }
    private void ImportOptionsChanged() { importOptionsVersion++; RefreshImportCommands(); }
    private void WiringChanged(PpmsWiringImportViewModel row)
    {
        if (!ImportWirings.Contains(row)) return;
        if (ChannelWiring is { } current && (!current.IsIncluded || current.Source is null)) ChannelWiring = null;
        OnPropertyChanged(nameof(ChannelWirings)); ImportOptionsChanged();
    }
    private bool CanImportPpms() => SupportsPpmsImport && CanValidate() && validated is not null && !string.IsNullOrWhiteSpace(PpmsPath)
        && (!UseRoutingProfiles || profilesReady && importedProfiles is not null && ImportMatrix is not null && ChannelWiring is not null);
    private bool CanLoadProfiles() => SupportsProfileImport && CanValidate() && !string.IsNullOrWhiteSpace(ChannelsPath)
        && !string.IsNullOrWhiteSpace(SamplesPath) && !string.IsNullOrWhiteSpace(InstrumentPortsPath);
    private bool CanCancelPpms() => !disposed && ppmsOperation is { IsCancellationRequested: false };
    private bool CanRestoreImport() => CanValidate() && importBackup is not null;
    private void RefreshImportCommands()
    {
        ImportPpmsCommand.NotifyCanExecuteChanged(); LoadImportProfilesCommand.NotifyCanExecuteChanged(); CancelPpmsCommand.NotifyCanExecuteChanged(); RestoreImportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ImportPreparationSummary));
        foreach (var wiring in ImportWirings) wiring.IsReadOnly = IsReadOnly;
    }
    private ImportRoutingSelection? CaptureRouting()
    {
        if (!UseRoutingProfiles) return null;
        var profiles = profilesReady ? importedProfiles : null;
        if (profiles is null) throw new InvalidOperationException("请重新载入有效路由档案。");
        var matrix = ImportMatrices.FirstOrDefault(m => m.Descriptor.Id == ImportMatrix?.Descriptor.Id) ?? throw new InvalidOperationException("请选择已注册的矩阵设备。");
        if (ChannelWiring is null || !ImportWirings.Contains(ChannelWiring) || !ChannelWiring.IsIncluded) throw new InvalidOperationException("请选择参与绑定的通道接线。");
        if (!double.TryParse(RouteSettleSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > (uint.MaxValue - 1) / 1000.0)
            throw new FormatException("路由稳定时间必须是有限、非负的秒数，且在计时器范围内。");
        return new(profiles, matrix.Descriptor.Id, ImportWirings.Where(w => w.IsIncluded).Select(w => w.Capture()), ChannelWiring.Layout.Id, TimeSpan.FromSeconds(seconds));
    }
    [RelayCommand(CanExecute = nameof(CanImportPpms))]
    private async Task ImportPpmsAsync()
    {
        if (!CanImportPpms()) return;
        var previous = validated!; var previousDirty = IsDirty; var previousText = DocumentText; var path = PpmsPath; var version = importOptionsVersion;
        try
        {
            var bindings = workspace.CreateConfiguredImportBindings(previous.Plan, CaptureRouting());
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); ppmsOperation = operation; HasError = false; IsDocumentBusy = true; RefreshImportCommands();
            try
            {
                var imported = await workspace.ImportAsync(path, bindings, operation.Token, previous.Plan.MaximumStepExecutions); operation.Token.ThrowIfCancellationRequested();
                if (DocumentText != previousText || NodeFragmentDirty || importOptionsVersion != version)
                { Message = "导入期间草稿或绑定已变化，结果未替换当前计划。"; return; }
                Apply(imported); importBackup = previous; importBackupDirty = previousDirty; IsDirty = true;
                Message = "PPMS 计划已导入并通过完整验证；尚未执行或保存，可恢复导入前计划。";
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { Message = "导入已取消，当前计划保留。"; }
            finally { ppmsOperation = null; IsDocumentBusy = false; RefreshImportCommands(); }
        }
        catch (Exception error) { HasError = true; Message = "导入未应用，当前计划保留：" + error.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanLoadProfiles))]
    private async Task LoadImportProfilesAsync()
    {
        if (!CanLoadProfiles()) return;
        var paths = (ChannelsPath, SamplesPath, InstrumentPortsPath); var version = profilePathVersion;
        profilesReady = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); ppmsOperation = operation; HasError = false; IsDocumentBusy = true; RefreshImportCommands();
        try
        {
            var profiles = await workspace.LoadRoutingProfilesAsync(paths.ChannelsPath, paths.SamplesPath, paths.InstrumentPortsPath, operation.Token); operation.Token.ThrowIfCancellationRequested();
            if (profilePathVersion != version) { ProfileSummary = "载入期间路径已变化，档案未应用。"; return; }
            importedProfiles = profiles; profilesReady = true; ImportWirings.Clear(); ChannelWiring = null;
            var sources = workspace.ImportDevices.Where(d => d.Category is ConfiguredDeviceCategory.SourceMeter or ConfiguredDeviceCategory.Source).Select(d => new PlanDeviceChoice(d.Descriptor)).ToArray();
            var meters = workspace.ImportDevices.Where(d => d.Category == ConfiguredDeviceCategory.Meter).Select(d => new PlanDeviceChoice(d.Descriptor)).ToArray();
            foreach (var layout in profiles.Wirings) ImportWirings.Add(new(layout, sources, meters, WiringChanged));
            ProfileSummary = $"{profiles.Channels.Count} 个通道 · {profiles.Samples.Count} 个样品 · 活动通道 {profiles.ActiveChannel.Value} · 活动样品 {profiles.ActiveSample.Value}";
            HasError = false; OnPropertyChanged(nameof(ChannelWirings)); ImportOptionsChanged();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { ProfileSummary = "档案载入已取消，请重新载入后导入。"; }
        catch (Exception error) { HasError = true; ProfileSummary = "档案未应用，请重新载入后导入：" + error.Message; }
        finally { ppmsOperation = null; IsDocumentBusy = false; RefreshImportCommands(); }
    }
    [RelayCommand(CanExecute = nameof(CanCancelPpms))]
    private void CancelPpms() { if (!CanCancelPpms()) return; ppmsOperation!.Cancel(); RefreshImportCommands(); }
    [RelayCommand(CanExecute = nameof(CanRestoreImport))]
    private void RestoreImport()
    {
        if (!CanRestoreImport()) return;
        try { var restored = workspace.Validate(importBackup!.Text); Apply(restored); IsDirty = importBackupDirty; importBackup = null; Message = "已恢复导入前计划。"; }
        catch (Exception error) { HasError = true; Message = "恢复未应用：" + error.Message; }
        RefreshImportCommands();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Notifications;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ExperimentPlanWorkspace workspace;
    private readonly Func<bool> parentCanEdit;
    private readonly Action changed;
    private readonly CancellationTokenSource lifetime = new();
    private ValidatedPlanDocument? validated;
    private bool disposed;
    private bool restoringDocument;
    private string? observedRunId;

    public PlanWorkspaceViewModel(ExperimentPlanWorkspace workspace, ExperimentPlan initialPlan, string path,
        Func<bool> canEdit, Action changed)
    {
        this.workspace = workspace;
        parentCanEdit = canEdit;
        this.changed = changed;
        documentPath = path;
        Apply(workspace.Inspect(initialPlan));
    }

    [ObservableProperty] private string documentText = "";
    [ObservableProperty] private string documentPath;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string planName = "未验证的草稿";
    [ObservableProperty] private string planSummary = "修改后请验证计划";
    [ObservableProperty] private bool isDocumentBusy;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string lastRunName = "尚未运行计划";
    [ObservableProperty] private string lastRunId = "—";
    [ObservableProperty] private string currentStep = "尚未执行";
    public ObservableCollection<PlanOutlineItem> Outline { get; } = new();
    public ObservableCollection<PlanRowItem> Rows { get; } = new();
    public bool CanEdit => !disposed && !IsDocumentBusy && parentCanEdit();
    public bool IsReadOnly => !CanEdit || HasUnappliedEdits;
    public bool IsValidated => validated is not null && !HasUnappliedEdits;
    public ExperimentPlan? ValidatedPlan => IsValidated ? validated?.Plan : null;
    public int AcquiredRows { get; private set; }
    public string RowCount => $"{AcquiredRows} 行 · 最近 200 行";

    partial void OnDocumentTextChanged(string? oldValue, string newValue)
    {
        if (restoringDocument) return;
        if (PreparationDirty || SampleDirty)
        {
            restoringDocument = true;
            try { DocumentText = oldValue ?? ""; }
            finally { restoringDocument = false; }
            HasError = true; Message = "设备准备或样品资料仍有未应用修改，请先应用或撤销。";
            return;
        }
        validated = null;
        IsDirty = true;
        HasError = false;
        Message = "草稿已修改；验证通过后才能运行。";
        PlanSummary = "尚未验证";
        Outline.Clear();
        ClearGraph();
        ClearPreparationEditor();
        ClearSampleEditor();
        RefreshCommands();
        changed();
    }
    partial void OnIsDocumentBusyChanged(bool value) { RefreshCommands(); changed(); }
    partial void OnDocumentPathChanged(string value) => RefreshCommands();

    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(IsValidated));
        ValidateCommand.NotifyCanExecuteChanged(); LoadCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
        RefreshGraphCommands();
        RefreshImportCommands();
        RefreshPreparationCommands();
        RefreshSampleCommands();
    }
    private bool CanValidate() => CanEdit && !HasUnappliedEdits;
    private bool CanAccessFile() => CanValidate() && !string.IsNullOrWhiteSpace(DocumentPath);

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private void Validate()
    {
        if (!CanValidate()) return;
        try { Apply(workspace.Validate(DocumentText)); Message = "计划验证通过，可运行或保存。"; }
        catch (Exception ex) { validated = null; HasError = true; Message = ex.Message; RefreshCommands(); changed(); }
    }

    [RelayCommand(CanExecute = nameof(CanAccessFile))]
    private async Task LoadAsync()
    {
        if (!CanAccessFile()) return;
        IsDocumentBusy = true;
        var previousText = DocumentText; var previousPath = DocumentPath;
        try
        {
            var document = await workspace.LoadAsync(previousPath, lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
            if (DocumentText != previousText || DocumentPath != previousPath || HasUnappliedEdits)
            { HasError = true; Message = "载入期间已有新修改；载入结果未应用，当前草稿保留。"; }
            else { Apply(document); IsDirty = false; Message = "已载入并验证计划。"; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"载入失败，草稿保留：{ex.Message}"; }
        finally { IsDocumentBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAccessFile))]
    private async Task SaveAsync()
    {
        if (!CanAccessFile()) return;
        IsDocumentBusy = true;
        var previousText = DocumentText; var previousPath = DocumentPath;
        try
        {
            var document = await workspace.SaveAsync(previousPath, previousText, lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
            if (DocumentText != previousText || DocumentPath != previousPath || HasUnappliedEdits)
            { HasError = true; Message = "文件已保存原计划快照；期间有新修改，当前草稿保留。"; }
            else { Apply(document); IsDirty = false; Message = "计划已保存。"; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { HasError = true; Message = $"保存失败：{ex.Message}"; }
        finally { IsDocumentBusy = false; }
    }

    private void Apply(ValidatedPlanDocument document)
    {
        ClearPreparationEditor();
        ClearSampleEditor();
        DocumentText = document.Text;
        validated = document;
        HasError = false;
        PlanName = document.Plan.Name;
        PlanSummary = $"{document.Preview.Devices.Count} 台设备 · {document.Preview.Schema.Columns.Count} 个数据列";
        Message = "计划已验证，可运行或保存。";
        Outline.Clear();
        AddStep(document.Plan.Root, 0);
        RebuildGraph(document.Plan);
        ReloadPreparationEditor();
        ReloadSampleEditor();
        RefreshCommands(); changed();
    }

    private void AddStep(ExperimentStep step, int depth)
    {
        var (title, detail) = step switch
        {
            SequenceStep s => ("顺序", $"{s.Children.Count} 个步骤"),
            RepeatStep s => ("重复", $"{s.Cycles} 次"),
            ScanStep s => ("扫描", $"{s.Axis.Device} / {s.Axis.Parameter} · {s.Values.Values.Count} 点"),
            ForEachStep s => ("迭代", $"{s.Variable} · {s.Values.Values.Count} 个值"),
            SetParameterStep s => ("设置", $"{s.Address.Device} / {s.Address.Parameter}"),
            AssignValueStep s => ("赋值", s.Variable.Value),
            SourceOutputStep s => (s.Enabled ? "打开输出" : "关闭输出", s.Device.Value),
            ReadStep s => ("联合读取", string.Join("、", s.Channels.Select(c => c.Column.Value))),
            ReadResistanceStep s => ("电阻读取", $"{s.Device} · {s.Function} · {s.ResistanceColumn}"),
            DelayStep s => ("等待", $"{s.Duration.TotalSeconds:0.###} s"),
            WaitStableStep s => ("等待稳定", $"{s.Readback.Parameter} · 最长 {s.Timeout.TotalSeconds:0.###} s"),
            IfElseStep s => ("条件分支", $"{s.Condition.Variable} {s.Condition.Operator}"),
            FallbackStep s => ("备选分支", $"{s.Children.Count} 个分支"),
            RetryStep s => ("重试", $"最多 {s.MaxAttempts} 次 · 间隔 {s.Delay.TotalSeconds:0.###} s"),
            TimeoutStep s => ("超时保护", $"{s.Timeout.TotalSeconds:0.###} s"),
            SubtreeStep s => ("子流程", $"{s.Children.Count} 个步骤"),
            WhileStep s => ("条件循环", s.Condition.Variable.Value),
            BranchOnOutcomeStep => ("按执行结果分支", step.Id.Value),
            ThresholdSwitchStep s => ("阈值选择", $"{s.Variable} · {s.Children.Count} 个分支"),
            ResultDecoratorStep s => ("结果转换", s.Effect switch { ResultEffect.ForceSuccess => "强制成功", ResultEffect.ForceFailure => "强制失败", _ => "反转结果" }),
            OutcomeMutationStep => ("结果回调", step.Id.Value),
            MutateValueStep s => ("变量修改", s.Variable.Value),
            ConditionStep s => ("条件检查", s.Condition.Variable.Value),
            CalculatedMeasurementStep s => ("读取后计算", string.Join(", ", s.Columns.Select(column => column.Column))),
            MeasurementGroupStep s => ("联合测量点", string.Join("、", s.Labels)),
            ReadSr830FrameStep s => ("SR830 原始帧", $"{s.Device} · {s.Input} · {s.Prefix}" + (s.IncludeReference ? " · 参考读回" : "")),
            ReadSourceSenseStep s => ("源与测量读数", s.Device.Value),
            ReadCurrentResistanceStep s => ("独立源电阻", $"{s.Source} / {s.Meter}"),
            ReadSampleResistanceStep => ("样品电阻", step.Id.Value),
            SampleAnalysisStep => ("样品分析", step.Id.Value),
            RouteMatrixStep => ("切换矩阵路由", step.Id.Value),
            VerifyMatrixRouteStep => ("检查矩阵路由", step.Id.Value),
            WaitFieldStep => ("等待磁场", step.Id.Value),
            WaitAngleStep => ("等待角度", step.Id.Value),
            WaitTemperatureStateStep => ("等待温度状态", step.Id.Value),
            WaitTemperatureStep => ("等待温度", step.Id.Value),
            TemperatureStabilityStep => ("温度稳定条件", step.Id.Value),
            TemperatureControlStep => ("温度控制", step.Id.Value),
            HardwareTemperatureSetpointStep => ("设置温度", step.Id.Value),
            SoftwareTemperatureRampStep => ("温度渐变", step.Id.Value),
            _ => ("实验步骤", step.Id.Value),
        };
        Outline.Add(new(step.Id, title, detail, depth));
        switch (step)
        {
            case SequenceStep sequence: foreach (var child in sequence.Children) AddStep(child, depth + 1); break;
            case RepeatStep repeat: AddStep(repeat.Body, depth + 1); break;
            case ScanStep scan: AddStep(scan.Body, depth + 1); break;
            case ForEachStep loop: AddStep(loop.Body, depth + 1); break;
            case IfElseStep conditional:
                AddStep(conditional.Then, depth + 1);
                if (conditional.Else is { } otherwise) AddStep(otherwise, depth + 1); break;
            case FallbackStep fallback: foreach (var child in fallback.Children) AddStep(child, depth + 1); break;
            case SubtreeStep subtree: foreach (var child in subtree.Children) AddStep(child, depth + 1); break;
            case ThresholdSwitchStep choice: foreach (var child in choice.Children) AddStep(child, depth + 1); break;
            case RetryStep retry: AddStep(retry.Body, depth + 1); break;
            case TimeoutStep timeout: AddStep(timeout.Body, depth + 1); break;
            case ResultDecoratorStep decorator: AddStep(decorator.Body, depth + 1); break;
            case WhileStep loop: AddStep(loop.Body, depth + 1); break;
            case OutcomeMutationStep hook:
                AddStep(hook.Body, depth + 1);
                if (hook.Success is { } success) AddStep(success, depth + 1);
                if (hook.Failure is { } failure) AddStep(failure, depth + 1); break;
            case BranchOnOutcomeStep branch:
                AddStep(branch.Condition, depth + 1); AddStep(branch.Then, depth + 1);
                if (branch.Else is { } alternative) AddStep(alternative, depth + 1); break;
            case CalculatedMeasurementStep calculated: AddStep(calculated.Body, depth + 1); break;
            case MeasurementGroupStep point: foreach (var member in point.Members) AddStep(member.Body, depth + 1); break;
            case SampleAnalysisStep analysis: foreach (var reading in analysis.Readings) AddStep(reading, depth + 1); break;
        }
    }

    public void BeginRun()
    {
        observedRunId = null; LastRunId = "—"; LastRunName = validated?.Plan.Name ?? "实验计划";
        Rows.Clear(); AcquiredRows = 0; OnPropertyChanged(nameof(RowCount)); CurrentStep = "准备设备";
        foreach (var node in Outline) node.IsActive = false;
        foreach (var node in GraphNodes) node.IsActive = false;
    }
    public void ApplyFact(WorkbenchNotification fact)
    {
        if (fact.Kind == WorkbenchNotificationKind.RunStarted) { observedRunId = fact.RunId; LastRunId = fact.RunId ?? "—"; }
        if (observedRunId is null || fact.RunId != observedRunId) return;
        if (fact.Kind == WorkbenchNotificationKind.StepStarted && fact.Step is { } step)
        {
            CurrentStep = step.Value;
            foreach (var node in Outline) node.IsActive = node.Id == step;
            foreach (var node in GraphNodes) node.IsActive = node.Id == step;
        }
        if (fact.Kind == WorkbenchNotificationKind.DataRowReceived && fact.Row is { } row)
        {
            AcquiredRows = fact.AcquiredRowCount ?? AcquiredRows + 1;
            Rows.Insert(0, new(AcquiredRows.ToString(CultureInfo.InvariantCulture), row.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
                string.Join("   ·   ", row.Values.Select(v => $"{v.Key}: {v.Value.Value.ToString("G6", CultureInfo.InvariantCulture)} {v.Value.Unit}"))));
            while (Rows.Count > 200) Rows.RemoveAt(Rows.Count - 1);
            OnPropertyChanged(nameof(RowCount));
        }
        if (fact.Kind is WorkbenchNotificationKind.RunCompleted or WorkbenchNotificationKind.RunAborted or WorkbenchNotificationKind.RunFaulted)
        {
            if (fact.AcquiredRowCount is { } total) { AcquiredRows = total; OnPropertyChanged(nameof(RowCount)); }
            foreach (var node in Outline) node.IsActive = false;
            foreach (var node in GraphNodes) node.IsActive = false;
            CurrentStep = fact.Message;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        if (LoadCommand.ExecutionTask is { } load) await load;
        if (SaveCommand.ExecutionTask is { } save) await save;
        if (ImportPpmsCommand.ExecutionTask is { } importing) await importing;
        if (LoadImportProfilesCommand.ExecutionTask is { } profiles) await profiles;
        lifetime.Dispose();
    }
}

public partial class PlanOutlineItem(StepId id, string title, string detail, int depth) : ObservableObject
{
    public StepId Id { get; } = id;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public Thickness Indent { get; } = new(Math.Min(depth, 8) * 12, 0, 0, 0);
    [ObservableProperty] private bool isActive;
}
public sealed record PlanRowItem(string Index, string Time, string Values);

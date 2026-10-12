using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanCalculatedColumnFormViewModel : ObservableObject
{
    public PlanCalculatedColumnFormViewModel(CalculatedColumn? column, Action changed, bool template)
    {
        this.changed = changed; columnId = template ? "" : column?.Column.Value ?? "";
        Expression = new(column?.Expression ?? new ConstantValue(new(0, PhysicalUnit.Dimensionless)), changed); if (column is null) Expression.NumberText = "";
    }
    private readonly Action changed;
    public PlanValueFormViewModel Expression { get; }
    [ObservableProperty] private string columnId;
    [ObservableProperty] private bool isReadOnly;
    partial void OnColumnIdChanged(string value) => changed();
    partial void OnIsReadOnlyChanged(bool value) => Expression.IsReadOnly = value;
    [ObservableProperty] private string previewSummary = "手填结果和表达式后应用并验证。";
    public void ApplyPreview(PlanCalculationColumnPreview? preview)
    {
        Expression.SetAvailableVariables(preview?.Inputs);
        PreviewSummary = preview is null ? "测量输入尚未完成，补全后刷新可用列。" : preview.Error is { } error ? "草稿待修正：" + error : "推导结果单位 / " + preview.Unit;
    }
    public CalculatedColumn Build() => new(new(ColumnId), Expression.Build());
}
public partial class PlanCalculatedMeasurementFormViewModel : ObservableObject
{
    private readonly CalculatedMeasurementStep original;
    private readonly Action changed;
    private readonly bool template;
    private bool loading = true;
    private bool refreshing;
    private Func<PlanCalculationDraft, PlanCalculationDraftPreview>? inspector;
    private PlanCalculationDraftPreview? preview;
    private readonly ObservableCollection<PlanCalculatedColumnFormViewModel> columns = new();
    public PlanCalculatedMeasurementFormViewModel(CalculatedMeasurementStep step, IReadOnlyList<DeviceDescriptor> devices, IReadOnlyList<AxisControllerDefinition> axes, RoutingConfiguration? routing, Action changed, bool template)
    {
        original = step; this.template = template; this.changed = changed; continueOnUnavailable = step.ContinueOnUnavailable; Columns = new(columns);
        BodyForm = new(step.Body, devices, Sr830BodyChanged, template, axes, routing);
        foreach (var column in step.Columns) AddForm(column, template); loading = false;
    }
    public PlanStepFormViewModel BodyForm { get; }
    public bool CanEditBody => BodyForm.IsSupported;
    public string InputSummary => preview is null ? "本次测量输入尚未验证；补全测量配置后显示列和单位。" : "本次行输入: " + string.Join(", ", preview.RowInputs.Take(16).Select(input => input.Id.Value + " / " + input.Type.Unit + (input.MayBeAbsent ? "（可缺失）" : ""))) + (preview.RowInputs.Count > 16 ? " … 共 " + preview.RowInputs.Count + " 列" : "") + "；仅当前测量行、坐标和前序结果。";
    [ObservableProperty] private string previewMessage = "";
    public bool HasPreviewMessage => PreviewMessage.Length != 0;
    partial void OnPreviewMessageChanged(string value) => OnPropertyChanged(nameof(HasPreviewMessage));
    public void SetDraftInspector(Func<PlanCalculationDraft, PlanCalculationDraftPreview>? value) { inspector = value; RefreshPreview(); }
    private ExperimentStep DraftBody() => !template && !BodyForm.IsDirty ? original.Body : CanEditBody ? BodyForm.Build() : original.Body;
    private void DraftChanged()
    {
        if (loading || refreshing) return;
        RefreshPreview(); GenerateSr830NominalCommand.NotifyCanExecuteChanged(); changed();
    }
    private void RefreshPreview()
    {
        if (loading || refreshing) return;
        refreshing = true;
        try
        {
            preview = null; PreviewMessage = "";
            if (inspector is null) { foreach (var column in Columns) column.ApplyPreview(null); return; }
            var draftColumns = Columns.Select(column =>
            {
                try { return column.Build(); }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException or ArithmeticException) { return null; }
            }).ToArray();
            preview = inspector(new(original.Id, DraftBody(), draftColumns));
            if (preview.Columns.Count != Columns.Count) throw new InvalidOperationException("计算草稿预览列数不一致。");
            for (var index = 0; index < Columns.Count; index++) Columns[index].ApplyPreview(preview.Columns[index]);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException or ArithmeticException)
        {
            preview = null; PreviewMessage = "请补全本次测量输入：" + ex.Message;
            foreach (var column in Columns) column.ApplyPreview(null);
        }
        finally { refreshing = false; OnPropertyChanged(nameof(InputSummary)); }
    }
    public ReadOnlyObservableCollection<PlanCalculatedColumnFormViewModel> Columns { get; }
    [ObservableProperty] private bool continueOnUnavailable;
    [ObservableProperty] private bool isReadOnly;
    partial void OnContinueOnUnavailableChanged(bool value) => changed();
    partial void OnIsReadOnlyChanged(bool value) { BodyForm.IsReadOnly = value; foreach (var column in Columns) column.IsReadOnly = value; AddColumnCommand.NotifyCanExecuteChanged(); RemoveColumnCommand.NotifyCanExecuteChanged(); GenerateSr830NominalCommand.NotifyCanExecuteChanged(); }
    private void AddForm(CalculatedColumn? originalColumn, bool template)
    {
        PlanCalculatedColumnFormViewModel? form = null; form = new(originalColumn, () => { if (columns.Contains(form!)) DraftChanged(); }, template); form.IsReadOnly = IsReadOnly; columns.Add(form);
    }
    private bool CanAddColumn() => !IsReadOnly && Columns.Count < 64;
    private bool CanRemoveColumn(PlanCalculatedColumnFormViewModel? column) => !IsReadOnly && Columns.Count > 1 && column is not null && columns.Contains(column);
    [RelayCommand(CanExecute = nameof(CanAddColumn))]
    private void AddColumn() { if (!CanAddColumn()) return; AddForm(null, true); DraftChanged(); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanRemoveColumn))]
    private void RemoveColumn(PlanCalculatedColumnFormViewModel? column) { if (!CanRemoveColumn(column)) return; columns.Remove(column!); DraftChanged(); RefreshCommands(); }
    private void RefreshCommands() { AddColumnCommand.NotifyCanExecuteChanged(); RemoveColumnCommand.NotifyCanExecuteChanged(); }
    public CalculatedMeasurementStep Build() => new(original.Id, DraftBody(), Columns.Select(column => column.Build()), ContinueOnUnavailable);
}

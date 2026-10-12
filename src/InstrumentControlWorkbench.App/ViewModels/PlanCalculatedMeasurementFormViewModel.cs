using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public CalculatedColumn Build() => new(new(ColumnId), Expression.Build());
}
public partial class PlanCalculatedMeasurementFormViewModel : ObservableObject
{
    private readonly CalculatedMeasurementStep original;
    private readonly Action changed;
    private readonly bool template;
    private readonly ObservableCollection<PlanCalculatedColumnFormViewModel> columns = new();
    public PlanCalculatedMeasurementFormViewModel(CalculatedMeasurementStep step, IReadOnlyList<DeviceDescriptor> devices, IReadOnlyList<AxisControllerDefinition> axes, RoutingConfiguration? routing, Action changed, bool template)
    {
        original = step; this.template = template; this.changed = changed; continueOnUnavailable = step.ContinueOnUnavailable; Columns = new(columns);
        BodyForm = new(step.Body, devices, changed, template, axes, routing);
        foreach (var column in step.Columns) AddForm(column, template);
    }
    public PlanStepFormViewModel BodyForm { get; }
    public bool CanEditBody => BodyForm.IsSupported;
    public string InputSummary => "输入列: " + string.Join(", ", original.InputColumns) + "；仅使用本次测量行和扫描轴。复杂测量的子节点可在流程图中单独编辑。";
    public ReadOnlyObservableCollection<PlanCalculatedColumnFormViewModel> Columns { get; }
    [ObservableProperty] private bool continueOnUnavailable;
    [ObservableProperty] private bool isReadOnly;
    partial void OnContinueOnUnavailableChanged(bool value) => changed();
    partial void OnIsReadOnlyChanged(bool value) { BodyForm.IsReadOnly = value; foreach (var column in Columns) column.IsReadOnly = value; AddColumnCommand.NotifyCanExecuteChanged(); RemoveColumnCommand.NotifyCanExecuteChanged(); }
    private void AddForm(CalculatedColumn? originalColumn, bool template)
    {
        PlanCalculatedColumnFormViewModel? form = null; form = new(originalColumn, () => { if (columns.Contains(form!)) changed(); }, template); form.IsReadOnly = IsReadOnly; columns.Add(form);
    }
    private bool CanAddColumn() => !IsReadOnly && Columns.Count < 64;
    private bool CanRemoveColumn(PlanCalculatedColumnFormViewModel? column) => !IsReadOnly && Columns.Count > 1 && column is not null && columns.Contains(column);
    [RelayCommand(CanExecute = nameof(CanAddColumn))]
    private void AddColumn() { if (!CanAddColumn()) return; AddForm(null, true); changed(); RefreshCommands(); }
    [RelayCommand(CanExecute = nameof(CanRemoveColumn))]
    private void RemoveColumn(PlanCalculatedColumnFormViewModel? column) { if (!CanRemoveColumn(column)) return; columns.Remove(column!); changed(); RefreshCommands(); }
    private void RefreshCommands() { AddColumnCommand.NotifyCanExecuteChanged(); RemoveColumnCommand.NotifyCanExecuteChanged(); }
    public CalculatedMeasurementStep Build() => new(original.Id, !template && !BodyForm.IsDirty ? original.Body : CanEditBody ? BodyForm.Build() : original.Body, Columns.Select(column => column.Build()), ContinueOnUnavailable);
}

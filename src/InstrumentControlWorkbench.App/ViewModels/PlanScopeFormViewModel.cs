using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanVariableBindingFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanVariableBindingFormViewModel(VariableBinding? binding, Action changed)
    { this.changed = changed; source = binding?.Source.Value ?? ""; destination = binding?.Destination.Value ?? ""; }
    [ObservableProperty] private string source;
    [ObservableProperty] private string destination;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSourceChanged(string value) => changed();
    partial void OnDestinationChanged(string value) => changed();
    public VariableBinding Build() => new(new(Source), new(Destination));
}

public partial class PlanVariableMapFormViewModel : ObservableObject
{
    private readonly ObservableCollection<PlanVariableBindingFormViewModel> rows = new();
    private readonly Action changed;
    public PlanVariableMapFormViewModel(string label, System.Collections.Generic.IReadOnlyList<VariableBinding> bindings, Action changed)
    {
        Label = label; this.changed = changed; Rows = new(rows);
        foreach (var binding in bindings) rows.Add(new(binding, changed));
    }
    public string Label { get; }
    public ReadOnlyObservableCollection<PlanVariableBindingFormViewModel> Rows { get; }
    [ObservableProperty] private bool isReadOnly;
    partial void OnIsReadOnlyChanged(bool value) { foreach (var row in rows) row.IsReadOnly = value; RefreshCommands(); }
    private bool CanAdd() => !IsReadOnly && rows.Count < 10_000;
    private bool CanRemove(PlanVariableBindingFormViewModel? row) => !IsReadOnly && row is not null && rows.Contains(row);
    private void RefreshCommands() { AddCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() { if (!CanAdd()) return; rows.Add(new(null, changed)); RefreshCommands(); changed(); }
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove(PlanVariableBindingFormViewModel? row) { if (!CanRemove(row)) return; rows.Remove(row!); RefreshCommands(); changed(); }
    public VariableBinding[] Build() => rows.Select(r => r.Build()).ToArray();
}

public partial class PlanScopeFormViewModel : ObservableObject
{
    private readonly SubtreeStep original;
    public PlanScopeFormViewModel(SubtreeStep step, Action changed)
    {
        original = step; Inputs = new("输入 / 父变量 → 局部变量", step.Inputs, changed); Outputs = new("输出 / 局部变量 → 父变量", step.Outputs, changed);
    }
    public PlanVariableMapFormViewModel Inputs { get; }
    public PlanVariableMapFormViewModel Outputs { get; }
    [ObservableProperty] private bool isReadOnly;
    partial void OnIsReadOnlyChanged(bool value) { Inputs.IsReadOnly = value; Outputs.IsReadOnly = value; }
    public SubtreeStep Build() => new(original.Id, original.Children, Inputs.Build(), Outputs.Build());
}

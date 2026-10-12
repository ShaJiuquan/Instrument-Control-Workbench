using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Routing;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanMatrixPortFormViewModel(string label, Action changed) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private string slotText = "";
    [ObservableProperty] private string rowText = "";
    [ObservableProperty] private bool isReadOnly;
    partial void OnSlotTextChanged(string value) => changed();
    partial void OnRowTextChanged(string value) => changed();
    public void Clear() { SlotText = ""; RowText = ""; }
    public MatrixPort Build() => new(PositiveInteger(SlotText), PositiveInteger(RowText));
    public static int PositiveInteger(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw new FormatException("矩阵卡槽、行和列必须是正整数。");
}
public partial class PlanInitialConnectionFormViewModel(string label, Action changed) : ObservableObject
{
    public ElectrodeLabel Electrode { get; } = new(label);
    [ObservableProperty] private string keyText = "";
    [ObservableProperty] private string slotText = "";
    [ObservableProperty] private string columnText = "";
    [ObservableProperty] private bool isReadOnly;
    partial void OnKeyTextChanged(string value) => changed();
    partial void OnSlotTextChanged(string value) => changed();
    partial void OnColumnTextChanged(string value) => changed();
    public void ClearAddress() { SlotText = ""; ColumnText = ""; }
    public ChannelConnection Build() => new(new(KeyText), PlanMatrixPortFormViewModel.PositiveInteger(SlotText), PlanMatrixPortFormViewModel.PositiveInteger(ColumnText));
}

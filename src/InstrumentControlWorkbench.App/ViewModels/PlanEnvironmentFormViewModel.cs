using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Devices;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanEnvironmentAxisChoice(AxisControllerDefinition Definition)
{
    public string Label => $"{Definition.Device.DisplayName} · {Definition.Device.Id} / {Definition.Readback.Id}";
}

public partial class PlanEnvironmentReadFormViewModel : ObservableObject
{
    private readonly EnvironmentRead? original;
    private readonly Action changed;
    private bool loading = true;
    private bool bindingEdited;
    public PlanEnvironmentReadFormViewModel(EnvironmentRole role, EnvironmentRead? original, IReadOnlyList<AxisControllerDefinition> axes, Action changed)
    {
        Role = role; this.original = original; this.changed = changed;
        Choices = Array.AsReadOnly(axes.Where(a => a.Readback.Unit == EnvironmentRead.Unit(role)).Select(a => new PlanEnvironmentAxisChoice(a)).ToArray());
        IsIncluded = original is not null; Column = original?.Column.Value ?? role.ToString().ToLowerInvariant(); BindController = original?.Address is not null;
        SelectedController = Choices.FirstOrDefault(c => Address(c.Definition) == original?.Address); loading = false;
    }
    public EnvironmentRole Role { get; }
    public string Label => Role switch { EnvironmentRole.Temperature => "温度 / Kelvin", EnvironmentRole.Field => "磁场 / Tesla", _ => "角度 / Degree" };
    public IReadOnlyList<PlanEnvironmentAxisChoice> Choices { get; }
    public string BindingSummary => BindController ? ResolveChoice() is { } choice ? choice.Label
        : !bindingEdited && original?.Address is { } address ? $"保持原绑定 {address.Device} / {address.Parameter}；当前目录未列出。" : "请选择该角色的已注册控制器读回。"
        : "未绑定控制器；记录缺失状态，不使用目标值替代读数。";
    public bool CanEdit => !IsReadOnly && IsIncluded;
    public bool CanSelectController => CanEdit && BindController;
    [ObservableProperty] private bool isIncluded;
    [ObservableProperty] private string column = "";
    [ObservableProperty] private bool bindController;
    [ObservableProperty] private PlanEnvironmentAxisChoice? selectedController;
    [ObservableProperty] private bool isReadOnly;
    partial void OnIsIncludedChanged(bool value) { Refresh(); MarkDirty(); }
    partial void OnColumnChanged(string value) => MarkDirty();
    partial void OnBindControllerChanged(bool value) { if (!loading) bindingEdited = true; Refresh(); MarkDirty(); }
    partial void OnSelectedControllerChanged(PlanEnvironmentAxisChoice? value) { if (!loading) bindingEdited = true; Refresh(); MarkDirty(); }
    partial void OnIsReadOnlyChanged(bool value) => Refresh();
    private void Refresh() { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSelectController)); OnPropertyChanged(nameof(BindingSummary)); }
    private void MarkDirty() { if (!loading) changed(); }
    private static ParameterAddress Address(AxisControllerDefinition axis) => new(axis.Device.Id, axis.Readback.Id);
    private PlanEnvironmentAxisChoice? ResolveChoice() => Choices.FirstOrDefault(c => Address(c.Definition) == (SelectedController is { } selected ? Address(selected.Definition) : null));
    public EnvironmentRead? Build()
    {
        if (!IsIncluded) return null;
        ParameterAddress? address = null;
        if (BindController) address = ResolveChoice() is { } choice ? Address(choice.Definition)
            : !bindingEdited && original?.Address is { } unchanged ? unchanged : throw new InvalidOperationException("请选择已注册控制器的实际读回参数。");
        return new(Role, new(Column), address);
    }
}

public partial class PlanEnvironmentFormViewModel : ObservableObject
{
    private readonly IReadOnlyList<EnvironmentRead> original;
    private bool edited;
    public PlanEnvironmentFormViewModel(IReadOnlyList<EnvironmentRead> reads, IReadOnlyList<AxisControllerDefinition> axes, Action changed)
    {
        original = reads;
        Entries = Array.AsReadOnly(Enum.GetValues<EnvironmentRole>().Select(role => new PlanEnvironmentReadFormViewModel(role, reads.SingleOrDefault(r => r.Role == role), axes, () => { edited = true; changed(); })).ToArray());
    }
    public IReadOnlyList<PlanEnvironmentReadFormViewModel> Entries { get; }
    [ObservableProperty] private bool isReadOnly;
    partial void OnIsReadOnlyChanged(bool value) { foreach (var entry in Entries) entry.IsReadOnly = value; }
    public IReadOnlyList<EnvironmentRead> Build()
    {
        // Preserve the authored order and exact unedited bindings. After edits,
        // surviving original roles stay in order, with newly added roles last.
        if (!edited) return original;
        var values = Entries.Select(e => e.Build()).OfType<EnvironmentRead>().ToDictionary(e => e.Role);
        return Array.AsReadOnly(original.Select(r => r.Role).Concat(Entries.Select(e => e.Role)).Distinct().Where(values.ContainsKey).Select(role => values[role]).ToArray());
    }
}

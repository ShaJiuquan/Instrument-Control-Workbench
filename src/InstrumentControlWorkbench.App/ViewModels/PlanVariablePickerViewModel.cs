using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.App.ViewModels;

public sealed record PlanVariableChoice(PlanVariableAvailability Variable)
{
    public string Id => Variable.Id.Value;
    public string DisplayName => $"{Id} · {Variable.Type.Unit?.ToString() ?? Variable.Type.Kind.ToString()} · {(Variable.IsActiveAxis ? "坐标 ID · " : "")}" +
        (Variable.MayBeAbsent ? "可缺失 · " : "") + "定义 " + string.Join(", ", Variable.PossibleDefinitions.Take(8).Select(step => step.Value)) + (Variable.PossibleDefinitions.Count > 8 ? " …" : "");
}

public partial class PlanVariablePickerViewModel : ObservableObject
{
    private IReadOnlyList<PlanVariableAvailability>? context;
    private readonly Action<string> selected;
    private readonly Func<PlanVariableAvailability, bool> accepts;
    private string currentId;
    private bool refreshing;
    public PlanVariablePickerViewModel(string id, Action<string> selected, Func<PlanVariableAvailability, bool>? accepts = null)
    { currentId = id; this.selected = selected; this.accepts = accepts ?? (_ => true); }
    public IReadOnlyList<PlanVariableChoice> Choices { get; private set; } = Array.Empty<PlanVariableChoice>();
    public string ContextHint => context is null ? "尚无已验证的位置上下文；可手填 ID 后验证。" : Choices.Count == 0 ? "此位置没有符合类型与单位的已定义变量。" : "从本位置可用变量中选择；定义步骤表示静态可能来源。";
    [ObservableProperty] private PlanVariableChoice? selectedChoice;
    [ObservableProperty] private bool isReadOnly;
    partial void OnSelectedChoiceChanged(PlanVariableChoice? value)
    {
        if (refreshing) return;
        if (IsReadOnly || value is null || !Choices.Contains(value)) { SyncId(currentId); return; }
        currentId = value.Id; selected(value.Id);
    }
    public void SetContext(IReadOnlyList<PlanVariableAvailability>? variables)
    { context = variables is null ? null : Array.AsReadOnly(variables.ToArray()); RefreshChoices(); }
    public void RefreshChoices()
    {
        Choices = Array.AsReadOnly((context ?? Array.Empty<PlanVariableAvailability>()).Where(accepts).Select(variable => new PlanVariableChoice(variable)).ToArray());
        OnPropertyChanged(nameof(Choices)); OnPropertyChanged(nameof(ContextHint)); SyncId(currentId);
    }
    public void SyncId(string id)
    {
        currentId = id; refreshing = true;
        try { SelectedChoice = Choices.FirstOrDefault(choice => choice.Id == id); }
        finally { refreshing = false; }
    }
}

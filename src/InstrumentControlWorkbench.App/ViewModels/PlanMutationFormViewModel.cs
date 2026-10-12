using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanMutationFormViewModel : ObservableObject
{
    private readonly MutateValueStep original;
    private readonly Action changed;
    private bool loading = true;
    public PlanMutationFormViewModel(MutateValueStep step, Action changed, bool template = false)
    {
        original = step; this.changed = changed;
        Variable = template ? "" : step.Variable.Value; Action = step.Action; NumberText = template ? "" : step.Value.Value.ToString("R", CultureInfo.InvariantCulture); Unit = step.Value.Unit;
        loading = false;
    }
    public string StepLabel => original.Id.Value;
    public VariableMutation[] Actions { get; } = Enum.GetValues<VariableMutation>();
    public PhysicalUnit[] Units { get; } = Enum.GetValues<PhysicalUnit>();
    public bool CanChooseUnit => !IsReadOnly && Action != VariableMutation.Multiply;
    public string UnitLabel => Action == VariableMutation.Multiply ? "乘数 / Dimensionless" : "数值 / " + Unit;
    [ObservableProperty] private string variable = "";
    [ObservableProperty] private VariableMutation action;
    [ObservableProperty] private string numberText = "";
    [ObservableProperty] private PhysicalUnit unit = PhysicalUnit.Dimensionless;
    [ObservableProperty] private bool isReadOnly;
    partial void OnVariableChanged(string value) => Changed();
    partial void OnActionChanged(VariableMutation value)
    {
        if (!loading) NumberText = "";
        OnPropertyChanged(nameof(CanChooseUnit)); OnPropertyChanged(nameof(UnitLabel)); Changed();
    }
    partial void OnNumberTextChanged(string value) => Changed();
    partial void OnUnitChanged(PhysicalUnit value) { OnPropertyChanged(nameof(UnitLabel)); Changed(); }
    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanChooseUnit));
    private void Changed() { if (!loading) changed(); }
    public MutateValueStep Build()
    {
        if (!double.TryParse(NumberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new FormatException("请输入有限数值，使用小数点和可选科学计数法。");
        return new(original.Id, new(Variable), Action, new(number, Action == VariableMutation.Multiply ? PhysicalUnit.Dimensionless : Unit));
    }
}

public partial class PlanOutcomeMutationFormViewModel : ObservableObject
{
    private readonly OutcomeMutationStep original;
    private readonly Action changed;
    public PlanOutcomeMutationFormViewModel(OutcomeMutationStep step, Action changed)
    {
        original = step; this.changed = changed; useSuccess = step.Success is not null; useFailure = step.Failure is not null;
        Success = new(step.Success ?? NewCallback(), changed, template: step.Success is null);
        Failure = new(step.Failure ?? NewCallback(), changed, template: step.Failure is null); RefreshReadonly();
    }
    public PlanMutationFormViewModel Success { get; }
    public PlanMutationFormViewModel Failure { get; }
    [ObservableProperty] private bool useSuccess;
    [ObservableProperty] private bool useFailure;
    [ObservableProperty] private bool isReadOnly;
    partial void OnUseSuccessChanged(bool value) { RefreshReadonly(); changed(); }
    partial void OnUseFailureChanged(bool value) { RefreshReadonly(); changed(); }
    partial void OnIsReadOnlyChanged(bool value) => RefreshReadonly();
    private void RefreshReadonly() { Success.IsReadOnly = IsReadOnly || !UseSuccess; Failure.IsReadOnly = IsReadOnly || !UseFailure; }
    private static MutateValueStep NewCallback() => new(new("callback-" + Guid.NewGuid().ToString("N")), new("requested"), VariableMutation.Set, new(0, PhysicalUnit.Dimensionless));
    public OutcomeMutationStep Build() => new(original.Id, original.Body, UseSuccess ? Success.Build() : null, UseFailure ? Failure.Build() : null);
}

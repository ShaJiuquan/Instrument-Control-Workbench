using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanConditionFormViewModel : ObservableObject
{
    private readonly Action changed;
    public PlanConditionFormViewModel(ValueCondition condition, Action changed)
    {
        this.changed = changed; variable = condition.Variable.Value;
        VariablePicker = new(variable, id => Variable = id); comparison = condition.Operator;
        useTolerance = condition.EqualityTolerance is not null;
        toleranceText = condition.EqualityTolerance?.Value.ToString("R", CultureInfo.InvariantCulture) ?? "";
        toleranceUnit = condition.EqualityTolerance?.Unit ?? (condition.Value is ConstantValue constant ? constant.Value.Unit : PhysicalUnit.Dimensionless);
        Value = new(condition.Value, changed, allowLogical: true);
    }
    public PlanVariablePickerViewModel VariablePicker { get; }
    public void SetAvailableVariables(IReadOnlyList<PlanVariableAvailability>? variables)
    { VariablePicker.SetContext(variables); Value.SetAvailableVariables(variables); }
    public PlanValueFormViewModel Value { get; }
    public ComparisonOperator[] Comparisons { get; } = Enum.GetValues<ComparisonOperator>();
    public PhysicalUnit[] Units { get; } = Enum.GetValues<PhysicalUnit>();
    public bool CanEditTolerance => !IsReadOnly && UseTolerance;
    [ObservableProperty] private string variable;
    [ObservableProperty] private ComparisonOperator comparison;
    [ObservableProperty] private bool useTolerance;
    [ObservableProperty] private string toleranceText;
    [ObservableProperty] private PhysicalUnit toleranceUnit;
    [ObservableProperty] private bool isReadOnly;
    partial void OnVariableChanged(string value) { VariablePicker.SyncId(value); changed(); }
    partial void OnComparisonChanged(ComparisonOperator value) => changed();
    partial void OnUseToleranceChanged(bool value) { OnPropertyChanged(nameof(CanEditTolerance)); changed(); }
    partial void OnToleranceTextChanged(string value) => changed();
    partial void OnToleranceUnitChanged(PhysicalUnit value) => changed();
    partial void OnIsReadOnlyChanged(bool value) { VariablePicker.IsReadOnly = value; Value.IsReadOnly = value; OnPropertyChanged(nameof(CanEditTolerance)); }
    public ValueCondition Build()
    {
        if (!Enum.IsDefined(Comparison)) throw new ArgumentOutOfRangeException(nameof(Comparison));
        Quantity? tolerance = null;
        if (UseTolerance)
        {
            if (!double.TryParse(ToleranceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed <= 0)
                throw new FormatException("相等容差必须是正有限数值。");
            tolerance = new(parsed, ToleranceUnit);
        }
        return new(new(Variable), Comparison, Value.Build(), tolerance);
    }
}

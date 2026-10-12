using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public enum PlanValueInputKind { Quantity, Variable, QualifiedNumber, Boolean, Text }

public partial class PlanValueFormViewModel : ObservableObject
{
    private readonly PlanValue original;
    private readonly Func<PhysicalUnit>? boundUnit;
    private readonly Action changed;
    private bool loading = true;
    private bool edited;
    public PlanValueFormViewModel(PlanValue value, Action changed, Func<PhysicalUnit>? boundUnit = null, bool allowLogical = false)
    {
        original = value; this.boundUnit = boundUnit; this.changed = changed;
        Kinds = allowLogical ? Enum.GetValues<PlanValueInputKind>() : [PlanValueInputKind.Quantity, PlanValueInputKind.Variable, PlanValueInputKind.QualifiedNumber];
        switch (value)
        {
            case ConstantValue constant: Kind = PlanValueInputKind.Quantity; NumberText = constant.Value.Value.ToString("R", CultureInfo.InvariantCulture); Unit = constant.Value.Unit; break;
            case VariableValue variable: Kind = PlanValueInputKind.Variable; Variable = variable.Variable.Value; break;
            case QuantityFromNumberValue qualified: Kind = PlanValueInputKind.QualifiedNumber; Variable = qualified.Variable.Value; Unit = qualified.Unit; break;
            case BooleanValue boolean: Kind = PlanValueInputKind.Boolean; Boolean = boolean.Value; break;
            case TextValue text: Kind = PlanValueInputKind.Text; Text = text.Value; break;
            default: throw new NotSupportedException("该值使用高级 JSON 编辑。");
        }
        loading = false;
    }
    public PlanValueInputKind[] Kinds { get; }
    public PhysicalUnit[] Units { get; } = Enum.GetValues<PhysicalUnit>();
    public bool HasNumber => Kind == PlanValueInputKind.Quantity;
    public bool HasVariable => Kind is PlanValueInputKind.Variable or PlanValueInputKind.QualifiedNumber;
    public bool HasBoolean => Kind == PlanValueInputKind.Boolean;
    public bool HasText => Kind == PlanValueInputKind.Text;
    public bool HasUnit => Kind is PlanValueInputKind.Quantity or PlanValueInputKind.QualifiedNumber;
    public bool CanChooseUnit => boundUnit is null && !IsReadOnly;
    public string UnitLabel
    {
        get
        {
            if (!HasUnit) return "变量引用保留原值类型与单位。";
            if (boundUnit is null) return "显式单位 / " + Unit;
            try { return "由设备参数决定 / " + boundUnit(); }
            catch (InvalidOperationException) { return "先选择设备参数以确定单位。"; }
        }
    }
    [ObservableProperty] private PlanValueInputKind kind;
    [ObservableProperty] private string numberText = "";
    [ObservableProperty] private string variable = "";
    [ObservableProperty] private PhysicalUnit unit = PhysicalUnit.Dimensionless;
    [ObservableProperty] private bool boolean;
    [ObservableProperty] private string text = "";
    [ObservableProperty] private bool isReadOnly;
    partial void OnKindChanged(PlanValueInputKind value)
    {
        OnPropertyChanged(nameof(HasNumber)); OnPropertyChanged(nameof(HasVariable)); OnPropertyChanged(nameof(HasBoolean)); OnPropertyChanged(nameof(HasText)); OnPropertyChanged(nameof(HasUnit)); Changed();
    }
    partial void OnNumberTextChanged(string value) => Changed();
    partial void OnVariableChanged(string value) => Changed();
    partial void OnUnitChanged(PhysicalUnit value) => Changed();
    partial void OnBooleanChanged(bool value) => Changed();
    partial void OnTextChanged(string value) => Changed();
    partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanChooseUnit));
    private void Changed() { OnPropertyChanged(nameof(UnitLabel)); if (!loading) { edited = true; changed(); } }
    public void AddressChanged()
    {
        edited = true; NumberText = ""; Variable = ""; OnPropertyChanged(nameof(UnitLabel));
    }
    public PlanValue Build()
    {
        if (!Kinds.Contains(Kind)) throw new InvalidOperationException("该位置不支持所选值类型。");
        if (!edited) return original;
        return Kind switch
        {
            PlanValueInputKind.Quantity => new ConstantValue(new(Scalar(NumberText), boundUnit?.Invoke() ?? Unit)),
            PlanValueInputKind.Variable => new VariableValue(new(Variable)),
            PlanValueInputKind.QualifiedNumber => new QuantityFromNumberValue(new(Variable), boundUnit?.Invoke() ?? Unit),
            PlanValueInputKind.Boolean => new BooleanValue(Boolean),
            PlanValueInputKind.Text => new TextValue(Text),
            _ => throw new InvalidOperationException("请选择支持的值类型。"),
        };
    }
    private static double Scalar(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("请输入有限数值，使用小数点和可选科学计数法。");
}

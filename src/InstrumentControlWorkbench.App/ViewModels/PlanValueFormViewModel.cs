using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public enum PlanValueInputKind { Quantity, Variable, QualifiedNumber, Boolean, Text, Arithmetic }

public partial class PlanValueFormViewModel : ObservableObject
{
    private readonly PlanValue original;
    private readonly Func<PhysicalUnit>? boundUnit;
    private readonly Action changed;
    private bool loading = true;
    private bool edited;
    private readonly int depth;
    public PlanValueFormViewModel(PlanValue value, Action changed, Func<PhysicalUnit>? boundUnit = null, bool allowLogical = false, int depth = 1)
    {
        if (depth < 1 || depth > ArithmeticValue.MaximumDepth || value is ArithmeticValue arithmeticValue && arithmeticValue.Depth + depth - 1 > ArithmeticValue.MaximumDepth) throw new ArgumentException("表达式层级超过允许范围。");
        this.depth = depth; original = value; this.boundUnit = boundUnit; this.changed = changed;
        Kinds = (allowLogical ? Enum.GetValues<PlanValueInputKind>() : [PlanValueInputKind.Quantity, PlanValueInputKind.Variable, PlanValueInputKind.QualifiedNumber, PlanValueInputKind.Arithmetic]).Where(kind => kind != PlanValueInputKind.Arithmetic || depth < ArithmeticValue.MaximumDepth).ToArray();
        switch (value)
        {
            case ArithmeticValue arithmetic: Kind = PlanValueInputKind.Arithmetic; SelectedOperator = arithmetic.Operation; CreateOperands(arithmetic.Left, arithmetic.Right, false); break;
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
    public bool HasArithmetic => Kind == PlanValueInputKind.Arithmetic;
    public ArithmeticOperator[] Operators { get; } = Enum.GetValues<ArithmeticOperator>();
    [ObservableProperty] private ArithmeticOperator? selectedOperator;
    [ObservableProperty] private PlanValueFormViewModel? left;
    [ObservableProperty] private PlanValueFormViewModel? right;
    partial void OnSelectedOperatorChanged(ArithmeticOperator? value) => Changed();
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
        if (!loading)
        {
            SelectedOperator = null; Left = null; Right = null;
            if (value == PlanValueInputKind.Arithmetic && depth < ArithmeticValue.MaximumDepth) CreateOperands(new ConstantValue(new(0, PhysicalUnit.Dimensionless)), new ConstantValue(new(0, PhysicalUnit.Dimensionless)), true);
        }
        OnPropertyChanged(nameof(HasArithmetic)); OnPropertyChanged(nameof(HasNumber)); OnPropertyChanged(nameof(HasVariable)); OnPropertyChanged(nameof(HasBoolean)); OnPropertyChanged(nameof(HasText)); OnPropertyChanged(nameof(HasUnit)); Changed();
    }
    partial void OnNumberTextChanged(string value) => Changed();
    partial void OnVariableChanged(string value) => Changed();
    partial void OnUnitChanged(PhysicalUnit value) => Changed();
    partial void OnBooleanChanged(bool value) => Changed();
    partial void OnTextChanged(string value) => Changed();
    partial void OnIsReadOnlyChanged(bool value) { if (Left is { } leftOperand) leftOperand.IsReadOnly = value; if (Right is { } rightOperand) rightOperand.IsReadOnly = value; OnPropertyChanged(nameof(CanChooseUnit)); }
    private void Changed() { OnPropertyChanged(nameof(UnitLabel)); if (!loading) { edited = true; changed(); } }
    public void AddressChanged()
    {
        edited = true; NumberText = ""; Variable = "";
        if (HasArithmetic) { SelectedOperator = null; CreateOperands(new ConstantValue(new(0, PhysicalUnit.Dimensionless)), new ConstantValue(new(0, PhysicalUnit.Dimensionless)), true); }
        OnPropertyChanged(nameof(UnitLabel));
    }
    private void CreateOperands(PlanValue leftValue, PlanValue rightValue, bool clear)
    {
        PlanValueFormViewModel? leftOperand = null; PlanValueFormViewModel? rightOperand = null;
        leftOperand = new(leftValue, () => { if (ReferenceEquals(Left, leftOperand)) Changed(); }, depth: depth + 1);
        rightOperand = new(rightValue, () => { if (ReferenceEquals(Right, rightOperand)) Changed(); }, depth: depth + 1);
        if (clear) { leftOperand.AddressChanged(); rightOperand.AddressChanged(); }
        leftOperand.IsReadOnly = IsReadOnly; rightOperand.IsReadOnly = IsReadOnly; Left = leftOperand; Right = rightOperand;
    }
    public PlanValue Build()
    {
        if (!Kinds.Contains(Kind)) throw new InvalidOperationException("该位置不支持所选值类型。");
        if (!edited) return original;
        return Kind switch
        {
            PlanValueInputKind.Arithmetic => new ArithmeticValue(SelectedOperator ?? throw new InvalidOperationException("请选择算术运算。"), Left?.Build() ?? throw new InvalidOperationException("填写左操作数。"), Right?.Build() ?? throw new InvalidOperationException("填写右操作数。")),
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

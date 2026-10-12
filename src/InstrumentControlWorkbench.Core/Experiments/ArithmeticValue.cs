using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public enum ArithmeticOperator { Add, Subtract, Multiply, Divide }

public sealed record ArithmeticValue : PlanValue
{
    public const int MaximumDepth = 16;
    public const int MaximumNodes = 255;
    public ArithmeticValue(ArithmeticOperator operation, PlanValue left, PlanValue right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        Depth = 1 + Math.Max(left is ArithmeticValue l ? l.Depth : 1, right is ArithmeticValue r ? r.Depth : 1);
        NodeCount = 1 + (left is ArithmeticValue a ? a.NodeCount : 1) + (right is ArithmeticValue b ? b.NodeCount : 1);
        if (Depth > MaximumDepth || NodeCount > MaximumNodes) throw new ArgumentException("Arithmetic exceeds the 16-level/255-node value budget.");
        Operation = operation; Left = left; Right = right;
        NumericPlanValue.TryConstant(this, out _);
    }
    public ArithmeticOperator Operation { get; }
    public PlanValue Left { get; }
    public PlanValue Right { get; }
    public int Depth { get; }
    public int NodeCount { get; }
}

// One pure evaluator/type contract is shared by compile, runtime and persisted
// evidence. It never reads a device or evaluates executable script text.
public static class NumericPlanValue
{
    public static PhysicalUnit InferUnit(PlanValue value, Func<ParameterId, PhysicalUnit> variableUnit) => value switch
    {
        ConstantValue constant => constant.Value.Unit,
        VariableValue variable => variableUnit(variable.Variable),
        QuantityFromNumberValue qualified when variableUnit(qualified.Variable) == PhysicalUnit.Dimensionless => qualified.Unit,
        QuantityFromNumberValue => throw new ArgumentException("Unit qualification requires a dimensionless numeric input."),
        ArithmeticValue arithmetic => QuantityArithmetic.Result(arithmetic.Operation, InferUnit(arithmetic.Left, variableUnit), InferUnit(arithmetic.Right, variableUnit)).Unit,
        _ => throw new ArgumentException("Arithmetic operands must be numeric quantities."),
    };
    public static Quantity Evaluate(PlanValue value, Func<ParameterId, Quantity> variable) => value switch
    {
        ConstantValue constant => constant.Value,
        VariableValue reference => variable(reference.Variable),
        QuantityFromNumberValue qualified => Qualify(variable(qualified.Variable), qualified.Unit),
        ArithmeticValue arithmetic => QuantityArithmetic.Evaluate(arithmetic.Operation, Evaluate(arithmetic.Left, variable), Evaluate(arithmetic.Right, variable)),
        _ => throw new ArgumentException("Arithmetic operands must be numeric quantities."),
    };
    private static Quantity Qualify(Quantity value, PhysicalUnit unit) => value.Unit == PhysicalUnit.Dimensionless ? new(value.Value, unit) : throw new ArgumentException("Unit qualification requires a dimensionless numeric input.");
    public static bool TryConstant(PlanValue value, out Quantity result)
    {
        if (value is ConstantValue constant) { result = constant.Value; return true; }
        if (value is VariableValue or QuantityFromNumberValue) { result = default; return false; }
        if (value is not ArithmeticValue arithmetic) throw new ArgumentException("Arithmetic operands must be numeric quantities.");
        var leftKnown = TryConstant(arithmetic.Left, out var left); var rightKnown = TryConstant(arithmetic.Right, out var right);
        if (arithmetic.Operation == ArithmeticOperator.Divide && rightKnown && right.Value == 0) throw new DivideByZeroException("Arithmetic denominator is zero.");
        if (leftKnown && rightKnown) { result = QuantityArithmetic.Evaluate(arithmetic.Operation, left, right); return true; }
        result = default; return false;
    }
    public static IReadOnlyList<ParameterId> Variables(PlanValue value)
    {
        var variables = new HashSet<ParameterId>(); Visit(value); return Array.AsReadOnly(variables.ToArray());
        void Visit(PlanValue item)
        {
            switch (item)
            {
                case ConstantValue: break;
                case VariableValue variable: variables.Add(variable.Variable); break;
                case QuantityFromNumberValue qualified: variables.Add(qualified.Variable); break;
                case ArithmeticValue arithmetic: Visit(arithmetic.Left); Visit(arithmetic.Right); break;
                default: throw new ArgumentException("Arithmetic operands must be numeric quantities.");
            }
        }
    }
}

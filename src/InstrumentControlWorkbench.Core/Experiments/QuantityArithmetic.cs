using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Experiments;

public static class QuantityArithmetic
{
    // Product relations define their corresponding quotient relations too.
    // KelvinPerMinute uses seconds as the time operand, with explicit scale.
    private static readonly (PhysicalUnit Left, PhysicalUnit Right, PhysicalUnit Product, double Scale)[] Products =
    [
        (PhysicalUnit.Ampere, PhysicalUnit.Ohm, PhysicalUnit.Volt, 1),
        (PhysicalUnit.Volt, PhysicalUnit.Ampere, PhysicalUnit.Watt, 1),
        (PhysicalUnit.Ampere, PhysicalUnit.Second, PhysicalUnit.Coulomb, 1),
        (PhysicalUnit.Ohm, PhysicalUnit.Meter, PhysicalUnit.OhmMeter, 1),
        (PhysicalUnit.TeslaPerSecond, PhysicalUnit.Second, PhysicalUnit.Tesla, 1),
        (PhysicalUnit.DegreePerSecond, PhysicalUnit.Second, PhysicalUnit.Degree, 1),
        (PhysicalUnit.KelvinPerMinute, PhysicalUnit.Second, PhysicalUnit.Kelvin, 1.0 / 60),
        (PhysicalUnit.Hertz, PhysicalUnit.Second, PhysicalUnit.Dimensionless, 1),
    ];
    public static (PhysicalUnit Unit, double Scale) Result(ArithmeticOperator operation, PhysicalUnit left, PhysicalUnit right)
    {
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(left) || !Enum.IsDefined(right)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operation is ArithmeticOperator.Add or ArithmeticOperator.Subtract)
            return left == right ? (left, 1) : throw new ArgumentException("Addition/subtraction requires matching units.");
        if (operation == ArithmeticOperator.Multiply)
        {
            if (left == PhysicalUnit.Dimensionless) return (right, 1);
            if (right == PhysicalUnit.Dimensionless) return (left, 1);
            foreach (var relation in Products)
                if (left == relation.Left && right == relation.Right || left == relation.Right && right == relation.Left) return (relation.Product, relation.Scale);
        }
        else
        {
            if (left == right) return (PhysicalUnit.Dimensionless, 1);
            if (right == PhysicalUnit.Dimensionless) return (left, 1);
            foreach (var relation in Products)
            {
                if (left == relation.Product && right == relation.Left) return (relation.Right, 1 / relation.Scale);
                if (left == relation.Product && right == relation.Right) return (relation.Left, 1 / relation.Scale);
            }
        }
        throw new ArgumentException($"The unit catalog has no result for {left} {operation} {right}.");
    }
    public static Quantity Evaluate(ArithmeticOperator operation, Quantity left, Quantity right)
    {
        var result = Result(operation, left.Unit, right.Unit);
        if (operation == ArithmeticOperator.Divide && right.Value == 0) throw new DivideByZeroException("Arithmetic denominator is zero.");
        var value = operation switch
        {
            ArithmeticOperator.Add => left.Value + right.Value,
            ArithmeticOperator.Subtract => left.Value - right.Value,
            ArithmeticOperator.Multiply => left.Value * right.Value * result.Scale,
            ArithmeticOperator.Divide => left.Value / right.Value * result.Scale,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (!double.IsFinite(value) || value == 0 && left.Value != 0 && right.Value != 0 && operation is ArithmeticOperator.Multiply or ArithmeticOperator.Divide)
            throw new ArithmeticException("Arithmetic overflowed or underflowed its finite numeric range.");
        return new(value, result.Unit);
    }
}

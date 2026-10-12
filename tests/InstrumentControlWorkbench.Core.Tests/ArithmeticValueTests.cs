using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class ArithmeticValueTests
{
    [Theory]
    [InlineData(ArithmeticOperator.Add, 3, PhysicalUnit.Volt, 2, PhysicalUnit.Volt, 5, PhysicalUnit.Volt)]
    [InlineData(ArithmeticOperator.Subtract, 3, PhysicalUnit.Kelvin, 2, PhysicalUnit.Kelvin, 1, PhysicalUnit.Kelvin)]
    [InlineData(ArithmeticOperator.Multiply, 3, PhysicalUnit.Dimensionless, 2, PhysicalUnit.Tesla, 6, PhysicalUnit.Tesla)]
    [InlineData(ArithmeticOperator.Multiply, 2, PhysicalUnit.Ampere, 3, PhysicalUnit.Ohm, 6, PhysicalUnit.Volt)]
    [InlineData(ArithmeticOperator.Multiply, 3, PhysicalUnit.Volt, 2, PhysicalUnit.Ampere, 6, PhysicalUnit.Watt)]
    [InlineData(ArithmeticOperator.Multiply, 3, PhysicalUnit.Second, 2, PhysicalUnit.Ampere, 6, PhysicalUnit.Coulomb)]
    [InlineData(ArithmeticOperator.Multiply, 3, PhysicalUnit.Meter, 2, PhysicalUnit.Ohm, 6, PhysicalUnit.OhmMeter)]
    [InlineData(ArithmeticOperator.Multiply, 3, PhysicalUnit.Second, 2, PhysicalUnit.TeslaPerSecond, 6, PhysicalUnit.Tesla)]
    [InlineData(ArithmeticOperator.Multiply, 3, PhysicalUnit.DegreePerSecond, 2, PhysicalUnit.Second, 6, PhysicalUnit.Degree)]
    [InlineData(ArithmeticOperator.Multiply, 2, PhysicalUnit.KelvinPerMinute, 30, PhysicalUnit.Second, 1, PhysicalUnit.Kelvin)]
    [InlineData(ArithmeticOperator.Multiply, 2, PhysicalUnit.Hertz, .5, PhysicalUnit.Second, 1, PhysicalUnit.Dimensionless)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.Volt, 2, PhysicalUnit.Ampere, 3, PhysicalUnit.Ohm)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.Volt, 2, PhysicalUnit.Ohm, 3, PhysicalUnit.Ampere)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.Watt, 2, PhysicalUnit.Ampere, 3, PhysicalUnit.Volt)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.Coulomb, 2, PhysicalUnit.Second, 3, PhysicalUnit.Ampere)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.OhmMeter, 2, PhysicalUnit.Meter, 3, PhysicalUnit.Ohm)]
    [InlineData(ArithmeticOperator.Divide, 1, PhysicalUnit.Kelvin, 30, PhysicalUnit.Second, 2, PhysicalUnit.KelvinPerMinute)]
    [InlineData(ArithmeticOperator.Divide, 1, PhysicalUnit.Dimensionless, 2, PhysicalUnit.Second, .5, PhysicalUnit.Hertz)]
    [InlineData(ArithmeticOperator.Divide, 1, PhysicalUnit.Dimensionless, 2, PhysicalUnit.Hertz, .5, PhysicalUnit.Second)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.Pascal, 2, PhysicalUnit.Pascal, 3, PhysicalUnit.Dimensionless)]
    [InlineData(ArithmeticOperator.Divide, 6, PhysicalUnit.OhmPerSquare, 2, PhysicalUnit.Dimensionless, 3, PhysicalUnit.OhmPerSquare)]
    public void ArithmeticInfersAndEvaluatesDeclaredUnitRelations(ArithmeticOperator operation, double left, PhysicalUnit leftUnit, double right, PhysicalUnit rightUnit, double expected, PhysicalUnit unit)
    {
        var expression = new ArithmeticValue(operation, new ConstantValue(new(left, leftUnit)), new ConstantValue(new(right, rightUnit)));
        Assert.Equal(unit, NumericPlanValue.InferUnit(expression, _ => throw new InvalidOperationException())); Assert.True(NumericPlanValue.TryConstant(expression, out var result)); Assert.Equal(expected, result.Value, 12); Assert.Equal(unit, result.Unit);
    }
    [Theory]
    [InlineData("mismatch")]
    [InlineData("unsupportedProduct")]
    [InlineData("logical")]
    [InlineData("text")]
    [InlineData("zero")]
    [InlineData("partialZero")]
    [InlineData("overflow")]
    [InlineData("underflow")]
    [InlineData("unknownOperator")]
    public void InvalidStaticArithmeticCannotEnterThePlan(string kind)
    {
        PlanValue left = new ConstantValue(new(kind == "overflow" ? double.MaxValue : kind == "underflow" ? 1e-300 : 1, PhysicalUnit.Volt));
        PlanValue right = new ConstantValue(new(kind == "zero" ? 0 : kind == "overflow" ? 2 : kind == "underflow" ? 1e-300 : 1, PhysicalUnit.Dimensionless)); var operation = ArithmeticOperator.Multiply;
        switch (kind)
        {
            case "mismatch": operation = ArithmeticOperator.Add; right = new ConstantValue(new(1, PhysicalUnit.Ampere)); break;
            case "unsupportedProduct": right = new ConstantValue(new(1, PhysicalUnit.Pascal)); break;
            case "logical": left = new BooleanValue(true); break;
            case "text": left = new TextValue("1"); break;
            case "zero": operation = ArithmeticOperator.Divide; break;
            case "partialZero": operation = ArithmeticOperator.Divide; left = new VariableValue(new("input")); right = new ConstantValue(new(0, PhysicalUnit.Dimensionless)); break;
            case "unknownOperator": operation = (ArithmeticOperator)999; break;
        }
        Assert.NotNull(Record.Exception(() => new ArithmeticValue(operation, left, right)));
    }
    [Fact]
    public void DynamicNestedValuesKeepNumericQualificationAndUnitInferenceConsistent()
    {
        var number = new ParameterId("number"); var current = new ParameterId("current");
        var expression = new ArithmeticValue(ArithmeticOperator.Divide, new ArithmeticValue(ArithmeticOperator.Multiply, new QuantityFromNumberValue(number, PhysicalUnit.Volt), new ConstantValue(new(2, PhysicalUnit.Dimensionless))), new VariableValue(current));
        var values = new Dictionary<ParameterId, Quantity> { [number] = new(3, PhysicalUnit.Dimensionless), [current] = new(.002, PhysicalUnit.Ampere) };
        Assert.False(NumericPlanValue.TryConstant(expression, out _)); Assert.Equal(PhysicalUnit.Ohm, NumericPlanValue.InferUnit(expression, id => values[id].Unit)); Assert.Equal(new Quantity(3000, PhysicalUnit.Ohm), NumericPlanValue.Evaluate(expression, id => values[id])); Assert.Equal(2, NumericPlanValue.Variables(expression).Count);
        values[number] = new(3, PhysicalUnit.Volt); Assert.Throws<ArgumentException>(() => NumericPlanValue.Evaluate(expression, id => values[id])); Assert.Throws<ArgumentException>(() => NumericPlanValue.InferUnit(expression, id => values[id].Unit));
    }
    [Fact]
    public void ValueBudgetsBoundLogicalOccurrencesEvenForSharedSubexpressions()
    {
        PlanValue chain = new VariableValue(new("x")); for (var i = 1; i < ArithmeticValue.MaximumDepth; i++) chain = new ArithmeticValue(ArithmeticOperator.Add, chain, new ConstantValue(new(1, PhysicalUnit.Dimensionless)));
        Assert.Equal(16, ((ArithmeticValue)chain).Depth); Assert.Throws<ArgumentException>(() => new ArithmeticValue(ArithmeticOperator.Add, chain, new ConstantValue(new(1, PhysicalUnit.Dimensionless))));
        PlanValue shared = new ConstantValue(new(0, PhysicalUnit.Dimensionless)); for (var i = 0; i < 7; i++) shared = new ArithmeticValue(ArithmeticOperator.Add, shared, shared);
        Assert.Equal(255, ((ArithmeticValue)shared).NodeCount); Assert.Throws<ArgumentException>(() => new ArithmeticValue(ArithmeticOperator.Add, shared, shared));
    }
}

using InstrumentControlWorkbench.Core.Data;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Tests;

public sealed class CalculatedMeasurementTests
{
    private static ReadSourceSenseStep Read => new(new("read"), new("source"), SourceFunction.Voltage, new("V"), new("I"));
    private static CalculatedColumn Power => new(new("P"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("V")), new VariableValue(new("I"))));
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("limit")]
    [InlineData("inputCollision")]
    [InlineData("noAcquisition")]
    [InlineData("multipleAcquisitions")]
    [InlineData("nested")]
    [InlineData("logical")]
    public void CalculationShapeRequiresOneBoundedAcquisitionAndDistinctNumericColumns(string damage)
    {
        Assert.NotNull(Record.Exception(() =>
        {
            var columns = damage switch { "empty" => Array.Empty<CalculatedColumn>(), "duplicate" => new[] { Power, Power }, "limit" => Enumerable.Range(0, 65).Select(i => new CalculatedColumn(new("p" + i), Power.Expression)).ToArray(), "inputCollision" => [new CalculatedColumn(new("V"), Power.Expression)], "logical" => [new CalculatedColumn(new("P"), new BooleanValue(true))], _ => new[] { Power } };
            ExperimentStep body = damage switch { "noAcquisition" => new DelayStep(new("delay"), TimeSpan.Zero), "multipleAcquisitions" => new SequenceStep(new("body"), [Read, new ReadSourceSenseStep(new("second"), new("source"), SourceFunction.Voltage, new("V2"), new("I2"))]), "nested" => new CalculatedMeasurementStep(new("inner"), Read, [Power]), _ => Read };
            return new CalculatedMeasurementStep(new("calculate"), body, columns);
        }));
    }
    [Fact]
    public void SummaryDefensivelyCopiesValuesAndProducesOrderedMissingResultsWithoutFabrication()
    {
        var step = new CalculatedMeasurementStep(new("calculate"), Read, [Power, new(new("ratio"), new ArithmeticValue(ArithmeticOperator.Divide, new VariableValue(new("V")), new VariableValue(new("I"))))], true);
        var values = new Dictionary<ParameterId, Quantity> { [new("V")] = new(1, PhysicalUnit.Volt), [new("I")] = new(0, PhysicalUnit.Ampere) }; var input = new MeasurementDataRow(DateTimeOffset.UtcNow, values); var row = CalculationSummary.BuildRow(step, 1, input); values[new("I")] = new(1, PhysicalUnit.Ampere);
        Assert.Equal(new Quantity(0, PhysicalUnit.Watt), row.Values[new("P")]); Assert.False(row.Values.ContainsKey(new("ratio"))); Assert.Equal(CalculationIssue.DivideByZero, row.Calculation!.Outcomes[1].Issue); Assert.Equal(0, input.Values[new("I")].Value);
        var missing = CalculationSummary.BuildRow(step, 1, new(input.Timestamp, new Dictionary<ParameterId, Quantity> { [new("V")] = new(1, PhysicalUnit.Volt) })); Assert.All(missing.Calculation!.Outcomes, outcome => Assert.Equal(CalculationIssue.MissingInput, outcome.Issue)); Assert.False(missing.Values.ContainsKey(new("P")));
    }
}

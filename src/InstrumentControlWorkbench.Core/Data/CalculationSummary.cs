using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.Core.Data;

public enum CalculationIssue { None, MissingInput, DivideByZero, UnrepresentableResult }
public sealed record CalculationOutcome(ParameterId Column, CalculationIssue Issue);
public sealed record CalculationSummary
{
    public const int CurrentAlgorithmVersion = 1;
    public CalculationSummary(StepId step, int inputRow, IEnumerable<CalculationOutcome> outcomes, int algorithmVersion = CurrentAlgorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(step); ArgumentNullException.ThrowIfNull(outcomes); var items = outcomes.ToArray();
        if (algorithmVersion != CurrentAlgorithmVersion) throw new NotSupportedException("Unsupported calculation algorithm version.");
        if (inputRow < 1 || items.Length is < 1 or > 64 || items.Any(outcome => outcome is null || outcome.Column is null || !Enum.IsDefined(outcome.Issue)) || items.Select(outcome => outcome.Column).Distinct().Count() != items.Length) throw new ArgumentException("Calculation evidence needs a prior input row and bounded distinct outcomes.");
        Step = step; InputRow = inputRow; Outcomes = Array.AsReadOnly(items); AlgorithmVersion = algorithmVersion;
    }
    public StepId Step { get; }
    public int InputRow { get; }
    public int AlgorithmVersion { get; }
    public IReadOnlyList<CalculationOutcome> Outcomes { get; }
    public static MeasurementDataRow BuildRow(CalculatedMeasurementStep step, int inputRow, MeasurementDataRow input)
    {
        if (input.Calculation is not null) throw new ArgumentException("Calculations cannot consume another calculated row.");
        var values = new Dictionary<ParameterId, Quantity>(input.Values); var outcomes = new List<CalculationOutcome>();
        foreach (var column in step.Columns)
        {
            if (values.ContainsKey(column.Column)) throw new ArgumentException("Calculated column collides with a persisted input.");
            var issue = CalculationIssue.None;
            if (NumericPlanValue.Variables(column.Expression).Any(variable => !values.ContainsKey(variable))) issue = CalculationIssue.MissingInput;
            else
            {
                try { values.Add(column.Column, NumericPlanValue.Evaluate(column.Expression, variable => values[variable])); }
                catch (DivideByZeroException) { issue = CalculationIssue.DivideByZero; }
                catch (ArithmeticException) { issue = CalculationIssue.UnrepresentableResult; }
            }
            outcomes.Add(new(column.Column, issue));
        }
        return new(input.Timestamp, values, input.Routing, input.Analysis, input.MeasurementGroup, input.Environment, input.Quality, input.IndependentResistance, input.AcquisitionFailures, new(step.Id, inputRow, outcomes), input.Sr830Ac);
    }
}
